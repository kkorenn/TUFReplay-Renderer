#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using FFmpeg.AutoGen;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace TUFReplayRenderer.Infrastructure.Render;

/// <summary>
/// A captured video frame on its way to the encoder thread.
/// </summary>
public struct CapturedFrame
{
  /// <summary>Zero-copy path: points into a pooled <see cref="NativeArray{T}"/>.</summary>
  public IntPtr NativePointer;

  public int Length;

  /// <summary>Fallback path (synthetic black frames) rented from <see cref="ArrayPool{T}"/>.</summary>
  public byte[]? ManagedBuffer;

  /// <summary>Non-null when the encoder thread must recycle a readback state after encoding.</summary>
  public ReadbackRequestState? ReadbackState;
}

/// <summary>
/// Receiver for completed readbacks. Implemented by the render session so the reader stays free of
/// session state.
/// </summary>
public interface IFrameSink
{
  bool IsCancelled { get; }

  void PushFrame(long frameIndex, CapturedFrame frame);

  void OnReadbackFailed(long frameIndex);
}

/// <summary>
/// Asynchronous GPU readback with a pooled set of persistent buffers. Ported from ADOFAIRenderer
/// (MIT), including its GPU colour-conversion path.
///
/// When the encoder opened with NV12 or YUV420P and the ColorConversion compute shader is
/// available, each frame is converted on the GPU and read back as 12-bpp planar data — 62% less
/// PCIe traffic than RGBA, no swscale work on the CPU, and correct colours regardless of the
/// RenderTexture's native channel order, because the shader samples the texture instead of
/// reinterpreting raw bytes. Anything else (10-bit encoders, missing shader) falls back to raw
/// readback converted by the encoder's swscale context.
/// </summary>
public sealed class RenderFrameReader : IDisposable
{
  private static readonly int HeightProperty = Shader.PropertyToID("Height");
  private static readonly int WidthProperty = Shader.PropertyToID("Width");
  private static readonly int DestinationBufferProperty = Shader.PropertyToID("DestinationBuffer");
  private static readonly int SourceTextureProperty = Shader.PropertyToID("SourceTexture");

  private const int InitialBufferSize = 16;

  private readonly Stack<ReadbackRequestState> _statePool = new Stack<ReadbackRequestState>();
  private readonly List<ReadbackRequestState> _allocatedStates = new List<ReadbackRequestState>();
  private readonly object _poolLock = new object();
  private readonly IFrameSink _sink;
  private readonly AVPixelFormat _targetFormat;

  private bool _isInitialized;
  private volatile bool _isDisposed;

  // ComputeBuffer/NativeArray may only be released on the main thread, but the encoder thread is
  // the one that finishes with a frame. States returned off-thread queue up here instead.
  private static readonly ConcurrentQueue<ReadbackRequestState> DeferredDisposals =
    new ConcurrentQueue<ReadbackRequestState>();
  private static int _mainThreadId = -1;

  public RenderFrameReader(IFrameSink sink, AVPixelFormat targetFormat)
  {
    _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    _targetFormat = targetFormat;
    _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    DrainDeferredDisposals();
    InitializePool();
  }

  internal static void DrainDeferredDisposals()
  {
    while (DeferredDisposals.TryDequeue(out ReadbackRequestState state))
      state.Dispose();
  }

  private void InitializePool()
  {
    lock (_poolLock)
    {
      if (_isInitialized)
        return;
      for (int i = 0; i < InitialBufferSize; i++)
      {
        ReadbackRequestState state = new ReadbackRequestState(this);
        _statePool.Push(state);
        _allocatedStates.Add(state);
      }
      _isInitialized = true;
    }
  }

  public void RequestFrameCapture(Texture texture, long videoFrameIndex)
  {
    if (_isDisposed || texture == null)
      return;

    ReadbackRequestState state;
    lock (_poolLock)
    {
      if (_isDisposed)
        return;
      if (_statePool.Count == 0)
      {
        state = new ReadbackRequestState(this);
        _allocatedStates.Add(state);
      }
      else
      {
        state = _statePool.Pop();
      }
      state.IsInFlight = true;
    }

    // GPU conversion requires the shader and an 8-bit 4:2:0 target; everything else falls back to
    // raw readback + swscale, which is a pure throughput difference, never a correctness one.
    ComputeShader? shader = RenderColorShader.Shader;
    bool useGpuConversion =
      shader != null
      && (_targetFormat == AVPixelFormat.AV_PIX_FMT_NV12 || _targetFormat == AVPixelFormat.AV_PIX_FMT_YUV420P);

    int rawBufferSize = texture.width * texture.height * 4;
    state.Setup(videoFrameIndex, rawBufferSize, useGpuConversion, texture.width, texture.height);

    if (useGpuConversion && state.GPUBuffer != null)
    {
      int kernelIndex = shader!.FindKernel(
        _targetFormat == AVPixelFormat.AV_PIX_FMT_NV12 ? "CSMain_NV12" : "CSMain_YUV420P"
      );
      if (kernelIndex >= 0)
      {
        shader.SetTexture(kernelIndex, SourceTextureProperty, texture);
        shader.SetBuffer(kernelIndex, DestinationBufferProperty, state.GPUBuffer);
        shader.SetInt(WidthProperty, texture.width);
        shader.SetInt(HeightProperty, texture.height);

        // NV12 writes 2 luma + interleaved chroma per lane, so its X grouping differs from planar.
        int threadGroupsX =
          _targetFormat == AVPixelFormat.AV_PIX_FMT_NV12 ? (texture.width + 31) / 32 : (texture.width + 63) / 64;
        int threadGroupsY = (texture.height + 15) / 16;

        shader.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, 1);
        AsyncGPUReadback.Request(state.GPUBuffer, state.CachedCallback);
        return;
      }
    }

    AsyncGPUReadback.RequestIntoNativeArray(ref state.NativeBuffer, texture, 0, state.CachedCallback);
  }

  internal void OnReadbackComplete(ReadbackRequestState state, AsyncGPUReadbackRequest request)
  {
    long frame = state.FrameIndex;

    lock (_poolLock)
    {
      state.IsInFlight = false;
      if (_isDisposed)
      {
        state.Dispose();
        return;
      }
    }

    if (_sink.IsCancelled)
    {
      RecycleState(state);
      return;
    }

    if (request.hasError)
    {
      // A frame index that never arrives stalls the ordered queue forever, so emit a synthetic one.
      _sink.OnReadbackFailed(frame);
      int byteSize = state.IsGpuConversion ? state.Width * state.Height * 3 / 2 : state.Width * state.Height * 4;
      byte[] blackFrame = ArrayPool<byte>.Shared.Rent(byteSize);
      Array.Clear(blackFrame, 0, byteSize);
      if (state.IsGpuConversion)
      {
        // All-zero NV12/YUV420P is bright green, not black: chroma neutral is 128.
        int lumaSize = state.Width * state.Height;
        for (int i = lumaSize; i < byteSize; i++)
          blackFrame[i] = 128;
      }
      _sink.PushFrame(frame, new CapturedFrame { ManagedBuffer = blackFrame, Length = byteSize });
      RecycleState(state);
      return;
    }

    unsafe
    {
      if (state.IsGpuConversion)
      {
        // The request's own view dies when this callback returns, so copy into the persistent
        // buffer the encoder thread will read from.
        NativeArray<byte> data = request.GetData<byte>();
        NativeArray<byte>.Copy(data, state.NativeBuffer, data.Length);
      }

      IntPtr pointer = (IntPtr)state.NativeBuffer.GetUnsafeReadOnlyPtr();
      _sink.PushFrame(
        frame,
        new CapturedFrame
        {
          NativePointer = pointer,
          Length = state.NativeBuffer.Length,
          ReadbackState = state,
        }
      );
    }
  }

  public void RecycleState(ReadbackRequestState state)
  {
    lock (_poolLock)
    {
      if (_isDisposed || !_isInitialized)
      {
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
          state.Dispose();
        else
          DeferredDisposals.Enqueue(state);
        return;
      }

      state.Recycle();
      _statePool.Push(state);
    }
  }

  public void Dispose()
  {
    lock (_poolLock)
    {
      if (_isDisposed)
        return;
      _isDisposed = true;
      foreach (ReadbackRequestState state in _allocatedStates)
      {
        // In-flight states are released by their own completion callback, which sees _isDisposed.
        if (!state.IsInFlight)
          state.Dispose();
      }
      _statePool.Clear();
      _allocatedStates.Clear();
      _isInitialized = false;
    }
  }
}

/// <summary>
/// One reusable readback slot. The buffer must outlive the request, so it is allocated with
/// <see cref="Allocator.Persistent"/>; TempJob allocations are invalidated after a few frames and
/// readback completion is asynchronous.
/// </summary>
public sealed class ReadbackRequestState : IDisposable
{
  private readonly RenderFrameReader _owner;

  public long FrameIndex { get; private set; }
  public NativeArray<byte> NativeBuffer;
  public bool IsInFlight { get; internal set; }
  public bool IsGpuConversion { get; private set; }
  public ComputeBuffer? GPUBuffer;
  public int Width { get; private set; }
  public int Height { get; private set; }

  /// <summary>Cached so the per-frame readback request does not allocate a delegate.</summary>
  public readonly Action<AsyncGPUReadbackRequest> CachedCallback;

  public ReadbackRequestState(RenderFrameReader owner)
  {
    _owner = owner;
    CachedCallback = OnComplete;
    FrameIndex = -1;
  }

  public void Setup(long frameIndex, int rawBufferSize, bool isGpuConversion, int width, int height)
  {
    FrameIndex = frameIndex;
    IsGpuConversion = isGpuConversion;
    Width = width;
    Height = height;

    // NV12/YUV420P are 12 bpp (w*h*3/2); raw RGBA/BGRA is 32 bpp.
    int readbackSize = isGpuConversion ? width * height * 3 / 2 : rawBufferSize;
    if (!NativeBuffer.IsCreated || NativeBuffer.Length != readbackSize)
    {
      if (NativeBuffer.IsCreated)
        NativeBuffer.Dispose();
      NativeBuffer = new NativeArray<byte>(readbackSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
    }

    if (isGpuConversion)
    {
      int computeBufferCount = readbackSize / 4;
      if (GPUBuffer != null && GPUBuffer.count == computeBufferCount)
        return;
      GPUBuffer?.Dispose();
      GPUBuffer = new ComputeBuffer(computeBufferCount, 4, ComputeBufferType.Raw);
    }
    else
    {
      GPUBuffer?.Dispose();
      GPUBuffer = null;
    }
  }

  private void OnComplete(AsyncGPUReadbackRequest request) => _owner.OnReadbackComplete(this, request);

  public void Recycle() => FrameIndex = -1;

  public void Dispose()
  {
    if (NativeBuffer.IsCreated)
      NativeBuffer.Dispose();
    GPUBuffer?.Dispose();
    GPUBuffer = null;
  }
}
