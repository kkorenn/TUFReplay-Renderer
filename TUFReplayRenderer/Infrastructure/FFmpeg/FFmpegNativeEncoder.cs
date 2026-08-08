#nullable enable
using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using TUFReplayRenderer.Domain.Render;
using TUFReplayRenderer.Infrastructure.Render;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace TUFReplayRenderer.Infrastructure.FFmpeg
{
  /// <summary>
  /// FFmpeg의 C 네이티브 API를 직접 호출(P/Invoke)하여 비디오를 인코딩하는 클래스입니다.
  ///
  /// ── 전체 동작 흐름 (Lifecycle) ──────────────────────────────────────
  ///
  ///   1. 생성자: 인코딩 설정(해상도, FPS, 코덱, 품질 등)을 저장합니다.
  ///
  ///   2. Start(outputPath):
  ///      a) 출력 컨테이너(mp4 등)를 위한 AVFormatContext 생성
  ///      b) 비디오 스트림(AVStream) 추가
  ///      c) 하드웨어 인코더(NVENC → AMF → QSV) → 소프트웨어 인코더(libx264 등)를
  ///         순서대로 시도하여 사용 가능한 인코더를 선택
  ///      d) 파일 헤더 작성 및 프레임/스케일러 버퍼 초기화
  ///
  ///   3. WriteFrame*(): 매 프레임마다 호출
  ///      a) Unity RGBA 원본 프레임을 sws_scale로 NV12/YUV420P로 변환
  ///      b) (QSV hw_frames 모드일 때) GPU 서피스로 업로드
  ///      c) avcodec_send_frame → avcodec_receive_packet → 파일 쓰기
  ///
  ///   4. End(): 인코더에 남아 있는 지연 프레임(delayed frames)을 플러시하고 트레일러 작성
  ///
  ///   5. Dispose(): 모든 FFmpeg 리소스(컨텍스트, 프레임, 스케일러, I/O) 해제
  ///
  /// ── 참고 문서 ──────────────────────────────────────────────────────
  ///   • FFmpeg 인코딩 개요: https://ffmpeg.org/doxygen/trunk/group__lavf__encoding.html
  ///   • Send/Receive 인코딩 API: https://ffmpeg.org/doxygen/trunk/group__lavc__encdec.html
  ///   • 하드웨어 가속 개요: https://ffmpeg.org/doxygen/trunk/group__lavu__hwcontext.html
  ///   • HW 가속 위키: https://trac.ffmpeg.org/wiki/HWAccelIntro
  /// </summary>
  public sealed unsafe class FFmpegNativeEncoder : IDisposable
  {
    /// <summary>
    /// 인코더에 전달할 설정값을 담는 구조체입니다.
    /// </summary>
    /// <summary>
    /// 오디오 인코딩에 전달할 설정값을 담는 구조체입니다.
    /// </summary>
    public struct AudioSettings
    {
      /// <summary>Track title written to the container (e.g. "Music"). Null keeps no title.</summary>
      public string Title;

      /// <summary>Marks this stream as the container's default audio track.</summary>
      public bool IsDefaultTrack;

      /// <summary>입력 오디오의 샘플 레이트 (Hz). 예: 44100, 48000.</summary>
      public int SampleRate;

      /// <summary>입력 오디오의 채널 수. 예: 1(모노), 2(스테레오).</summary>
      public int Channels;

      /// <summary>
      /// 입력 오디오의 샘플 포맷. 기본값은 AV_SAMPLE_FMT_FLT (32-bit float).
      /// Unity의 AudioClip.GetData()는 float[] 형태이므로 FLT가 적합합니다.
      /// </summary>
      public AVSampleFormat InputSampleFormat;

      /// <summary>
      /// 사용할 오디오 코덱 ID. 기본값(0)이면 AAC를 사용합니다.
      /// 예: AV_CODEC_ID_AAC, AV_CODEC_ID_FLAC, AV_CODEC_ID_OPUS 등.
      /// </summary>
      public AVCodecID AudioCodecId;

      /// <summary>
      /// 오디오 비트레이트 (bps 단위). 기본값(0)이면 128000 bps를 사용합니다.
      /// AAC의 경우 128000~320000 bps가 일반적입니다.
      /// </summary>
      public int Bitrate;
    }

    public struct EncoderSettings
    {
      /// <summary>출력 비디오의 가로 해상도 (픽셀).</summary>
      public int Width;

      /// <summary>출력 비디오의 세로 해상도 (픽셀).</summary>
      public int Height;

      /// <summary>초당 프레임 수 (예: 60.0, 29.97 등).</summary>
      public double FPS;

      /// <summary>사용할 비디오 코덱 종류 (H264, H265, AV1, VP9).</summary>
      public Codec Codec;

      /// <summary>비트레이트 제어 모드 (CQP, CBR, VBR).</summary>
      public RateControlMode Mode;

      /// <summary>CQP 모드에서 사용되는 품질 값. 값이 낮을수록 고품질. (예: H.264 CRF 18 ≈ 시각적 무손실)</summary>
      public int QualityValue; // CRF

      /// <summary>VBR/CBR 모드의 목표 비트레이트 (kbps 단위). FFmpeg 내부에서 bps로 변환됩니다.</summary>
      public int TargetBitrate; // VBR

      /// <summary>VBR 모드의 최대 비트레이트 (kbps 단위). 피크 품질 한도를 결정합니다.</summary>
      public int MaxBitrate; // VBR

      /// <summary>GOP(Group of Pictures) 크기 (초). 키프레임 간격을 의미합니다.</summary>
      public double GopSize;

      /// <summary>소프트웨어 CPU 인코더를 강제 사용할지 여부.</summary>
      public bool ForceSoftware;

      /// <summary>
      /// 오디오 인코딩 설정. null이면 오디오 스트림 없이 비디오만 인코딩합니다 (기존 동작과 동일).
      /// 값을 설정하면 비디오와 오디오가 함께 먹싱(muxing)됩니다.
      /// </summary>
      public AudioSettings? Audio;

      /// <summary>
      /// Additional audio tracks written to the container alongside <see cref="Audio"/>. mp4/mkv
      /// carry multiple audio streams; players play the default track, editors expose all of them.
      /// </summary>
      public AudioSettings[] ExtraAudioTracks;

      /// <summary>
      /// Pixel layout of raw (non pre-converted) frames fed to the encoder. Unity's readback
      /// returns bytes in the RenderTexture's native order, which is BGRA rather than RGBA on
      /// Metal; feeding BGRA through an RGBA-configured swscale swaps red and blue in the output.
      /// Null keeps the historical RGBA behaviour.
      /// </summary>
      public AVPixelFormat? RawSourceFormat;
    }

    /// <summary>Resolved raw-input pixel format for this session.</summary>
    private AVPixelFormat RawSourceFormat => _settings.RawSourceFormat ?? AVPixelFormat.AV_PIX_FMT_RGBA;

    // ──────────────────────────────────────────────────────────────
    // FFmpeg 핵심 구조체 포인터들
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 출력 컨테이너(mp4, mkv 등)의 포맷 정보를 관리하는 컨텍스트.
    /// 파일 헤더/트레일러 작성, 패킷 기록 등에 사용됩니다.
    /// 참고: https://ffmpeg.org/doxygen/trunk/group__lavf__core.html
    /// </summary>
    private AVFormatContext* _pFormatContext = null;

    /// <summary>
    /// 코덱(인코더)의 상태를 관리하는 컨텍스트.
    /// 해상도, 픽셀 포맷, 프레임레이트, 품질 설정 등 인코딩에 필요한 모든 파라미터가 여기에 설정됩니다.
    /// 참고: https://ffmpeg.org/doxygen/trunk/structAVCodecContext.html
    /// </summary>
    private AVCodecContext* _pCodecContext = null;

    /// <summary>
    /// 출력 파일 내의 비디오 스트림.
    /// 하나의 컨테이너(포맷 컨텍스트)에 여러 스트림(비디오, 오디오 등)이 포함될 수 있는데,
    /// 여기서는 비디오 스트림 하나만 사용합니다.
    /// 참고: https://ffmpeg.org/doxygen/trunk/group__lavf__core.html
    /// </summary>
    private AVStream* _pVideoStream = null;

    // ──────────────────────────────────────────────────────────────
    // 프레임 버퍼 및 색공간 변환기(스케일러) 포인터들
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Unity에서 캡처한 원본 RGBA 프레임 데이터를 가리키는 프레임 구조체.
    /// 실제 픽셀 데이터를 소유하지 않고, data[0]과 linesize[0]로 외부 버퍼를 참조합니다.
    /// 참고: https://ffmpeg.org/doxygen/trunk/group__lavu__frame.html
    /// </summary>
    private AVFrame* _pSrcFrame = null;

    /// <summary>
    /// 인코더가 요구하는 픽셀 포맷(YUV420P 또는 NV12)으로 변환된 프레임.
    /// av_frame_get_buffer()로 실제 픽셀 데이터 메모리가 할당됩니다.
    /// </summary>
    private AVFrame* _pDstFrame = null;

    /// <summary>
    /// libswscale의 색공간/해상도 변환 컨텍스트.
    /// RGBA(Unity) → YUV420P/NV12(인코더) 변환을 수행합니다.
    /// 해상도 변환 없이 색공간만 변환할 때도 sws_scale을 사용합니다.
    /// 참고: https://ffmpeg.org/doxygen/trunk/group__libsws.html
    /// </summary>
    private SwsContext* _pSwsContext = null;

    /// <summary>현재까지 인코딩한 프레임의 누적 인덱스. PTS(Presentation Time Stamp) 값으로 사용됩니다.</summary>
    private long _frameIndex;

    /// <summary>Start()가 성공적으로 완료되어 인코딩이 가능한 상태인지 여부.</summary>
    private bool _isInitialized;

    /// <summary>Dispose()가 이미 호출되었는지 여부. 중복 해제를 방지합니다.</summary>
    private bool _disposed;

    /// <summary>멀티스레딩 환경에서 인코더 상태 및 리소스 접근을 동기화하기 위한 락 객체.</summary>
    private readonly object _lock = new();

    /// <summary>
    /// QSV 인코더에서 hw_frames_ctx 모드(GPU 프레임 풀)가 활성화되었는지 여부.
    /// true이면 매 프레임마다 NV12 → QSV 하드웨어 서피스 업로드가 필요합니다.
    /// false이면 시스템 메모리의 NV12 프레임을 직접 인코더에 전달합니다.
    /// </summary>
    private bool _isQsvEncoder;

    /// <summary>
    /// 인코더의 목표 픽셀 포맷.
    /// - 일반 인코더(NVENC, AMF, 소프트웨어): YUV420P (가장 범용적인 4:2:0 크로마 서브샘플링)
    /// - QSV 인코더: NV12 (Y 평면 + UV 인터리브 평면, Intel 하드웨어가 선호하는 포맷)
    ///
    /// 참고 - 주요 픽셀 포맷 관계:
    ///   RGBA (Unity 원본, 4채널 8비트)
    ///     ↓ sws_scale 변환
    ///   YUV420P (Y/U/V 3개 평면, 크로마 1/4 해상도) 또는
    ///   NV12 (Y 1개 평면 + UV 인터리브 1개 평면, 크로마 1/4 해상도)
    ///
    /// 참고: https://ffmpeg.org/doxygen/trunk/pixfmt_8h.html
    /// </summary>
    private AVPixelFormat _dstPixFmt = AVPixelFormat.AV_PIX_FMT_YUV420P;

    /// <summary>최종 선택되어 구동된 인코더의 픽셀 포맷입니다.</summary>
    public AVPixelFormat SelectedPixelFormat => _dstPixFmt;

    /// <summary>Name of the encoder that actually opened, e.g. "h264_videotoolbox".</summary>
    public string? SelectedEncoderName { get; private set; }

    /// <summary>
    /// 인코딩된 패킷 데이터를 임시로 담을 패킷 구조체.
    /// 매 프레임마다 할당/해제하는 오버헤드를 방지하기 위해 멤버 변수로 캐싱합니다.
    /// </summary>
    private AVPacket* _pPacket = null;

    /// <summary>
    /// QSV 하드웨어 가속 모드일 때 사용할 GPU 프레임 구조체.
    /// 매 프레임마다 할당/해제하는 오버헤드를 방지하기 위해 멤버 변수로 캐싱합니다.
    /// </summary>
    private AVFrame* _pHwFrame = null;
    private bool _loggedConversionMode;

    // ──────────────────────────────────────────────────────────────
    // 오디오 인코딩 관련 FFmpeg 구조체 포인터들
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 오디오 코덱(인코더)의 상태를 관리하는 컨텍스트.
    /// 샘플 레이트, 채널 레이아웃, 샘플 포맷 등 오디오 인코딩 파라미터가 설정됩니다.
    /// </summary>
    /// <summary>Everything one audio stream owns. The encoder writes N of these.</summary>
    private sealed class AudioTrackState
    {
      public AVCodecContext* CodecContext;
      public AVStream* Stream;
      public SwrContext* Swr;
      public AVAudioFifo* Fifo;
      public AVFrame* Frame;
      public long SampleIndex;
    }

    private readonly System.Collections.Generic.List<AudioTrackState> _audioTracks =
      new System.Collections.Generic.List<AudioTrackState>();

    /// <summary>오디오 인코딩된 패킷 데이터를 임시로 담을 패킷 구조체.</summary>
    private AVPacket* _pAudioPacket = null;

    /// <summary>오디오 인코딩이 활성화되어 있는지 여부.</summary>
    private bool _audioEnabled;

    /// <summary>생성 시 전달받은 인코딩 설정값 (불변).</summary>
    private readonly EncoderSettings _settings;

    public FFmpegNativeEncoder(EncoderSettings settings)
    {
      _settings = settings;
    }

    // ================================================================
    // 유틸리티 메서드
    // ================================================================

    /// <summary>
    /// 애플리케이션의 Codec enum 값을 FFmpeg 내부에서 사용하는 AVCodecID로 변환합니다.
    /// AVCodecID는 "어떤 코덱인지(예: H.264, AV1)"를 식별하는 값이고,
    /// 실제 인코더 구현체(예: libx264, h264_nvenc)는 별도로 선택됩니다.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    // ReSharper disable once UnusedMember.Local
    private AVCodecID GetAVCodecID()
    {
      return _settings.Codec switch
      {
        Codec.H264 => AVCodecID.AV_CODEC_ID_H264,
        Codec.H265 => AVCodecID.AV_CODEC_ID_HEVC,
        Codec.AV1 => AVCodecID.AV_CODEC_ID_AV1,
        Codec.VP9 => AVCodecID.AV_CODEC_ID_VP9,
        Codec.VVC => AVCodecID.AV_CODEC_ID_VVC,
        Codec.ProRes => AVCodecID.AV_CODEC_ID_PRORES,
        _ => throw new ArgumentOutOfRangeException(),
      };
    }

    /// <summary>
    /// 선택된 코덱에 대해 시도할 인코더 이름 목록을 우선순위 순으로 반환합니다.
    ///
    /// 우선순위: 하드웨어 인코더(NVENC → AMF → QSV) → 소프트웨어 인코더(libx264 등)
    ///
    /// - nvenc: NVIDIA GPU 전용 하드웨어 인코더 (CUDA 기반)
    /// - amf:   AMD GPU 전용 하드웨어 인코더 (Advanced Media Framework)
    /// - qsv:   Intel GPU/iGPU 전용 하드웨어 인코더 (Quick Sync Video, Media SDK)
    /// - lib*:  CPU 기반 소프트웨어 인코더 (항상 사용 가능하지만 느림)
    ///
    /// Start()에서 이 목록을 순회하며 첫 번째로 구동 가능한 인코더를 사용합니다.
    /// </summary>
    private string[] GetEncoderCandidates()
    {
      return GPUUtil.GetPrioritizedEncoderCandidates(_settings.Codec, _settings.ForceSoftware).ToArray();
    }

    /// <summary>
    /// FFmpeg 에러 코드(음수 정수)를 사람이 읽을 수 있는 문자열로 변환합니다.
    /// 예: -1 → "Operation not permitted"
    ///
    /// 내부적으로 av_strerror()를 호출하여 스택 할당된 버퍼에 메시지를 기록합니다.
    /// 참고: https://ffmpeg.org/doxygen/trunk/group__lavu__error.html
    /// </summary>
    private string GetFFmpegErrorString(int errnum)
    {
      const ulong bufferSize = 1024;
      byte* buffer = stackalloc byte[(int)bufferSize];
      if (ffmpeg.av_strerror(errnum, buffer, bufferSize) >= 0)
      {
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"알 수 없는 에러 (Code: {errnum})";
      }
      return $"알 수 없는 에러 (Code: {errnum})";
    }

    // ================================================================
    // 인코더 초기화 (Start)
    // ================================================================

    /// <summary>
    /// 인코딩 파이프라인 전체를 초기화합니다.
    ///
    /// 수행 단계:
    ///   1. 출력 파일의 컨테이너 포맷 컨텍스트(AVFormatContext) 생성
    ///   2. 비디오 스트림(AVStream) 추가
    ///   3. 인코더 후보 순회 → 코덱 컨텍스트(AVCodecContext) 설정 → 인코더 열기
    ///   4. 스트림에 코덱 파라미터 복사
    ///   5. 출력 파일 열기 및 헤더 작성
    ///   6. 프레임 버퍼(AVFrame) 및 색공간 변환기(SwsContext) 초기화
    ///
    /// 실패 시 Dispose()를 호출하여 부분 초기화된 리소스를 모두 정리한 뒤 예외를 재발생시킵니다.
    /// </summary>
    /// <param name="outputPath">출력 비디오 파일 경로 (예: "C:\output\video.mp4")</param>
    public void Start(string outputPath)
    {
      lock (_lock)
      {
        if (_isInitialized || _disposed)
          return;

        try
        {
          // ── 1단계: 출력 컨테이너 포맷 컨텍스트 생성 ──
          // avformat_alloc_output_context2는 파일 확장자(outputPath)를 보고
          // 적합한 출력 포맷(mp4, mkv 등)을 자동으로 선택합니다.
          // 참고: https://ffmpeg.org/doxygen/trunk/group__lavf__core.html
          fixed (AVFormatContext** ppFormatCtx = &_pFormatContext)
          {
            ffmpeg.avformat_alloc_output_context2(ppFormatCtx, null, null, outputPath);
          }

          if (_pFormatContext == null)
            throw new Exception("출력 포맷 컨텍스트를 생성하지 못했습니다.");

          // ── 2단계: 비디오 스트림 추가 ──
          // 컨테이너 안에 비디오 트랙(스트림)을 하나 생성합니다.
          // 오디오가 필요하면 여기서 오디오 스트림을 추가로 만들 수 있습니다.
          // 참고: https://ffmpeg.org/doxygen/trunk/group__lavf__core.html
          _pVideoStream = ffmpeg.avformat_new_stream(_pFormatContext, null);
          if (_pVideoStream == null)
            throw new Exception("비디오 스트림을 생성하지 못했습니다.");

          // ── 3단계: 인코더 선택 및 열기 ──
          // GetEncoderCandidates()가 반환하는 우선순위 목록을 순회하며
          // 첫 번째로 사용 가능한 인코더를 찾습니다.
          var candidates = GetEncoderCandidates();
          var openedSuccessfully = false;

          foreach (var encoderName in candidates)
          {
            // 이전 후보(QSV 등)가 남긴 하드웨어 상태를 초기화합니다.
            // 초기화하지 않으면 QSV 초기화 성공 후 avcodec_open2 실패 → 소프트웨어 인코더로
            // 폴백했을 때 WriteFrame이 QSV 경로를 타서 null hw_frames_ctx를 역참조합니다.
            _isQsvEncoder = false;
            if (_pHwFrame != null)
            {
              var pStaleHwFrame = _pHwFrame;
              ffmpeg.av_frame_free(&pStaleHwFrame);
              _pHwFrame = null;
            }

            // avcodec_find_encoder_by_name: 이름(예: "h264_nvenc")으로 인코더를 검색합니다.
            // 해당 인코더가 빌드에 포함되어 있지 않으면 null을 반환합니다.
            // 참고: https://ffmpeg.org/doxygen/trunk/group__lavc__encoding.html
            var pCodec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
            if (pCodec == null)
              continue;

            // avcodec_alloc_context3: 선택된 인코더용 코덱 컨텍스트를 할당합니다.
            // 이 컨텍스트에 해상도, 프레임레이트, 품질 등 모든 인코딩 파라미터를 설정합니다.
            // 참고: https://ffmpeg.org/doxygen/trunk/group__lavc__core.html
            _pCodecContext = ffmpeg.avcodec_alloc_context3(pCodec);
            if (_pCodecContext == null)
              continue;

            _pCodecContext->strict_std_compliance = ffmpeg.FF_COMPLIANCE_EXPERIMENTAL;

            // ── 기본 비디오 파라미터 설정 ──
            _pCodecContext->width = _settings.Width;
            _pCodecContext->height = _settings.Height;
            _pCodecContext->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            // thread_count = 0: FFmpeg이 시스템 CPU 코어 수에 맞춰 자동으로 스레드를 할당합니다.
            // libvvenc의 경우에도 FFmpeg 래퍼가 thread_count를 VVenC 내부 스레드 수로 전달하므로,
            // 0(자동)을 설정해야 모든 코어를 활용한 병렬 인코딩이 가능합니다.
            _pCodecContext->thread_count = 0;

            // ── 픽셀 포맷 설정 ──
            // 비디오 인코더는 일반적으로 YUV420P 또는 NV12 형식의 입력을 기대합니다.
            //
            // • YUV420P: Y/U/V 3개의 독립 평면(plane). 대부분의 소프트웨어 인코더가 사용.
            // • NV12:    Y 평면 + UV 인터리브 평면. 하드웨어 인코더(NVENC, AMF, QSV)가 선호.
            // • QSV 특수 사항: 코덱 컨텍스트의 pix_fmt은 AV_PIX_FMT_QSV(하드웨어 서피스 식별자)로,
            //   실제 sws_scale 출력은 NV12(소프트웨어 포맷)로 설정합니다.
            //   FFmpeg이 내부적으로 NV12 ↔ QSV 서피스 간 매핑을 처리합니다.
            //
            // 참고: https://ffmpeg.org/doxygen/trunk/pixfmt_8h.html
            var selectedPixFmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            if (encoderName.Contains("qsv"))
            {
              selectedPixFmt = AVPixelFormat.AV_PIX_FMT_NV12;
              _pCodecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_QSV;
            }
            else if (
              encoderName.Contains("nvenc")
              || encoderName.Contains("amf")
              || encoderName.Contains("videotoolbox")
            )
            {
              // VideoToolbox(macOS)는 소프트웨어 프레임(NV12)을 직접 받아
              // 내부적으로 하드웨어 서피스에 업로드하므로 별도의 hw_device_ctx가 필요 없습니다.
              selectedPixFmt = AVPixelFormat.AV_PIX_FMT_NV12;
              _pCodecContext->pix_fmt = selectedPixFmt;
            }
            else if (encoderName == "libvvenc")
            {
              // libvvenc: 10비트 YUV420P10LE만 지원 (이 빌드에서 yuv420p 미지원 확인됨)
              selectedPixFmt = AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
              _pCodecContext->pix_fmt = selectedPixFmt;
            }
            else if (encoderName == "prores_videotoolbox")
            {
              // VideoToolbox ProRes: BGRA 입력을 받아 하드웨어에서 직접 변환/인코딩합니다.
              // 소스가 Unity RGBA이므로 크로마 서브샘플링 없이 전달되어 품질 손실이 없습니다.
              selectedPixFmt = AVPixelFormat.AV_PIX_FMT_BGRA;
              _pCodecContext->pix_fmt = selectedPixFmt;
            }
            else if (encoderName == "prores_ks")
            {
              // prores_ks(소프트웨어): ProRes 422 표준인 10비트 4:2:2 입력 사용
              selectedPixFmt = AVPixelFormat.AV_PIX_FMT_YUV422P10LE;
              _pCodecContext->pix_fmt = selectedPixFmt;
            }
            else
            {
              _pCodecContext->pix_fmt = selectedPixFmt;
            }

            // ── 프레임레이트 및 타임베이스 설정 ──
            // time_base: 프레임 간 시간 간격의 기본 단위.
            //   예) 60fps → time_base = 1/60초 (num=1, den=60)
            //   PTS(Presentation Time Stamp) 값에 time_base를 곱하면 실제 시간(초)이 됩니다.
            //   PTS=150, time_base=1/60 → 150 * (1/60) = 2.5초
            //
            // framerate: 인코더에 힌트를 주는 프레임레이트 값 (time_base의 역수).
            //
            // RationalUtil: FPS(double)를 분수 형태(분자/분모)로 변환합니다.
            //   예) 29.97 → 30000/1001 (NTSC 표준)
            //
            // 참고: https://ffmpeg.org/doxygen/trunk/structAVCodecContext.html
            var fpsRational = RationalUtil.Double2MaxPrecisionInt(_settings.FPS);

            // NTSC
            if (Math.Abs(_settings.FPS - 29.97) < 0.001)
              fpsRational = (30000, 1001);
            if (Math.Abs(_settings.FPS - 59.94) < 0.001)
              fpsRational = (60000, 1001);
            if (Math.Abs(_settings.FPS - 23.976) < 0.001 || Math.Abs(_settings.FPS - 23.98) < 0.001)
              fpsRational = (24000, 1001);

            _pCodecContext->time_base = new AVRational { num = fpsRational.Denominator, den = fpsRational.Numerator };
            _pCodecContext->framerate = new AVRational { num = fpsRational.Numerator, den = fpsRational.Denominator };

            // ── GOP(Group of Pictures) 크기 설정 ──
            // GOP: 키프레임(I-frame) 간격. 예) gop_size=120, 60fps → 2초마다 키프레임.
            // 키프레임은 완전한 이미지를 담고 있어 탐색(seek) 지점이 됩니다.
            // 너무 크면 탐색 정밀도가 떨어지고, 너무 작으면 파일 크기가 증가합니다.
            var calculatedGop = Math.Max(1, (int)Math.Round(_settings.FPS * _settings.GopSize));
            if (encoderName == "libvvenc")
            {
              // libvvenc (VVenC) requires IntraPeriod (gop_size) to be a multiple of its GOPSize (typically 32).
              _pCodecContext->gop_size = Math.Max(32, ((calculatedGop + 15) / 32) * 32);
            }
            else
            {
              _pCodecContext->gop_size = calculatedGop;
            }

            // ── B-프레임 비활성화 ──
            // B-프레임(양방향 예측 프레임): 앞뒤 프레임을 참조하여 압축률을 높이지만,
            // 인코딩 지연(latency)이 증가하고 일부 인코더에서 호환성 문제가 발생할 수 있습니다.
            // 실시간 녹화 시나리오에서는 비활성화하여 지연을 최소화합니다.
            //
            // 참고: has_b_frames는 인코더가 내부적으로 설정하는 읽기 전용 필드이므로
            //       직접 세팅하지 않습니다.
            //
            // SVT-AV1 특수 사항: max_b_frames를 AVCodecContext에서 직접 설정하면
            // "bad parameter" 에러가 발생하므로, svtav1-params를 통해 제어합니다.
            // pred-struct=1: Low Delay (IPPP) 구조로 B-프레임 비활성화
            if (encoderName == "libsvtav1")
            {
              ffmpeg.av_opt_set(_pCodecContext->priv_data, "svtav1-params", "pred-struct=1", 0);
            }
            else if (encoderName == "libvvenc")
            {
              // libvvenc relies on hierarchical GOP structures; forcing max_b_frames = 0 causes initialization failure.
              // We explicitly set max_b_frames to -1 to allow libvvenc to use its default GOP reference distance.
              _pCodecContext->max_b_frames = -1;
            }
            else
            {
              _pCodecContext->max_b_frames = 0;
            }

            // ── 글로벌 헤더 플래그 ──
            // 일부 컨테이너 포맷(mp4 등)은 코덱의 설정 정보(SPS/PPS 등)를
            // 각 패킷이 아닌 파일 헤더에 한 번만 기록하길 요구합니다.
            // AVFMT_GLOBALHEADER 플래그가 설정된 포맷이면 AV_CODEC_FLAG_GLOBAL_HEADER를 켜서
            // 인코더가 extradata에 설정 정보를 저장하도록 합니다.
            // 참고: https://ffmpeg.org/doxygen/trunk/avformat_8h.html
            if ((_pFormatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
              _pCodecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            // ── 품질/비트레이트 설정 적용 ──
            ApplyQualitySettings(encoderName);

            // ── QSV 인코더 하드웨어 디바이스 초기화 ──
            // QSV(Quick Sync Video)는 Intel GPU의 고정 기능 인코딩 하드웨어를 사용합니다.
            // FFmpeg에서 QSV를 사용하려면 하드웨어 디바이스 컨텍스트를 생성해야 합니다.
            //
            // 아래에서 3가지 전략을 순차적으로 시도합니다:
            //   전략 1) child_device_type=d3d11va 옵션으로 QSV 디바이스 직접 생성
            //          → FFmpeg 내부에서 D3D11 디바이스를 자식으로 생성하여 QSV에 연결
            //   전략 2) D3D11VA 디바이스를 먼저 만들고, QSV 디바이스를 파생(derive)
            //          → 일부 드라이버에서 전략 1이 실패할 때 유효
            //   전략 3) QSV 디바이스 직접 생성 (레거시 방식)
            //          → 구형 드라이버/시스템에서 폴백
            //
            // 디바이스 확보 후, GPU 프레임 풀 활용을 시도합니다:
            //   hw_frames_ctx: GPU VRAM에 프레임 풀을 사전 할당 (제로 카피, 최고 성능)
            //   hw_device_ctx: 시스템 메모리에서 인코더가 직접 프레임을 읽음 (호환성 우수)
            //
            // 참고: https://ffmpeg.org/doxygen/trunk/group__lavu__hwcontext.html
            if (encoderName.Contains("qsv"))
            {
              AVBufferRef* hwDeviceCtx = null;
              var qsvReady = false;
              int err;

              var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
              string childDeviceName = isWindows ? "d3d11va" : "vaapi";
              AVHWDeviceType childDeviceType = isWindows
                ? AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA
                : AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI;

              // ── 전략 1: child_device_type 옵션 ──
              // av_hwdevice_ctx_create에 "child_device_type" 딕셔너리를 전달하면
              // FFmpeg이 내부적으로 적절한 하위 디바이스(Windows: D3D11, Linux: VAAPI)를 생성하고 QSV 세션에 연결합니다.
              {
                AVDictionary* opts = null;
                ffmpeg.av_dict_set(&opts, "child_device_type", childDeviceName, 0);
                err = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, null, opts, 0);
                ffmpeg.av_dict_free(&opts);
                if (err >= 0)
                {
                  qsvReady = true;
                  RenderLog.Info($"{encoderName}: child_device_type={childDeviceName} 방식으로 QSV 디바이스 생성 성공");
                }
                else
                {
                  RenderLog.Warn(
                    $"{encoderName}: child_device_type 방식 실패 — {GetFFmpegErrorString(err)} (Code: {err})"
                  );
                }
              }

              // ── 전략 2: 하위 디바이스 → QSV 파생(derive) ──
              // 하위 디바이스(D3D11VA / VAAPI)를 먼저 생성한 뒤,
              // av_hwdevice_ctx_create_derived로 QSV 디바이스를 파생시킵니다.
              if (!qsvReady)
              {
                AVBufferRef* childDeviceCtx = null;
                err = ffmpeg.av_hwdevice_ctx_create(&childDeviceCtx, childDeviceType, null, null, 0);
                if (err >= 0)
                {
                  err = ffmpeg.av_hwdevice_ctx_create_derived(
                    &hwDeviceCtx,
                    AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
                    childDeviceCtx,
                    0
                  );
                  if (err >= 0)
                  {
                    qsvReady = true;
                    RenderLog.Info($"{encoderName}: {childDeviceType} → QSV 파생 방식으로 디바이스 생성 성공");
                  }
                  ffmpeg.av_buffer_unref(&childDeviceCtx);
                }
              }

              // ── 전략 3: QSV 직접 생성 (레거시) ──
              if (!qsvReady)
              {
                err = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, null, null, 0);
                if (err >= 0)
                  qsvReady = true;
              }

              if (!qsvReady)
              {
                RenderLog.Warn($"{encoderName}: 모든 QSV 디바이스 생성 전략 실패. 다음 후보 코덱을 테스트합니다.");
                {
                  var pCtx = _pCodecContext;
                  ffmpeg.avcodec_free_context(&pCtx);
                }
                _pCodecContext = null;
                continue;
              }

              // ── hw_frames_ctx 시도: GPU 프레임 풀 사전 생성 ──
              // AVHWFramesContext는 GPU VRAM에 프레임 풀을 미리 할당하여
              // 매 프레임마다 메모리 할당/해제 오버헤드를 제거합니다.
              //
              // 장점: 제로 카피 가능, 최고 성능
              // 단점: GPU/드라이버에 따라 해상도/포맷 제약으로 실패할 수 있음
              //       (예: Intel Arc에서 특정 텍스처 크기 제약 → E_INVALIDARG)
              //
              // initial_pool_size: 미리 할당할 프레임 수. 인코더 파이프라인 깊이에 맞춤.
              // 해상도를 32 배수로 정렬: QSV 하드웨어가 요구하는 텍스처 정렬 조건.
              //
              // 참고:
              //   AVHWFramesContext: https://ffmpeg.org/doxygen/trunk/structAVHWFramesContext.html
              //   av_hwframe_ctx_alloc: https://ffmpeg.org/doxygen/trunk/group__lavu__hwcontext.html
              bool framesCtxReady = false;
              AVBufferRef* hwFramesRef = ffmpeg.av_hwframe_ctx_alloc(hwDeviceCtx);
              if (hwFramesRef != null)
              {
                var framesCtx = (AVHWFramesContext*)hwFramesRef->data;
                framesCtx->format = AVPixelFormat.AV_PIX_FMT_QSV; // 하드웨어 서피스 포맷
                framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12; // 소프트웨어 측 포맷
                framesCtx->width = (_settings.Width + 31) & ~31; // 32 배수 정렬 (비트 마스크)
                framesCtx->height = (_settings.Height + 31) & ~31; // 32 배수 정렬 (비트 마스크)
                framesCtx->initial_pool_size = 4; // 프레임 풀 크기

                err = ffmpeg.av_hwframe_ctx_init(hwFramesRef);
                if (err >= 0)
                {
                  // _pHwFrame을 먼저 할당합니다. 실패 시 예외를 던지면 hwFramesRef/hwDeviceCtx가
                  // 해제되지 않고 누수되므로, 할당 실패는 hw_device_ctx 모드 폴백으로 처리합니다.
                  _pHwFrame = ffmpeg.av_frame_alloc();
                  if (_pHwFrame != null)
                  {
                    // 성공: 코덱 컨텍스트에 hw_frames_ctx를 연결합니다.
                    // av_buffer_ref로 참조 카운트를 증가시켜 hwFramesRef 해제 후에도 유효하게 합니다.
                    _pCodecContext->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFramesRef);
                    _isQsvEncoder = true;
                    framesCtxReady = true;
                    RenderLog.Info($"{encoderName}: hw_frames_ctx 초기화 성공 (GPU 프레임 풀 모드)");
                  }
                  else
                  {
                    RenderLog.Warn(
                      $"{encoderName}: 하드웨어 프레임 구조체 할당 실패 — hw_device_ctx 모드로 폴백합니다."
                    );
                  }
                }
                else
                {
                  RenderLog.Warn(
                    $"{encoderName}: hw_frames_ctx 실패 — {GetFFmpegErrorString(err)} (Code: {err}). hw_device_ctx 모드로 폴백합니다."
                  );
                }
                ffmpeg.av_buffer_unref(&hwFramesRef);
              }

              // ── hw_frames_ctx 실패 시: hw_device_ctx + NV12 시스템 메모리 폴백 ──
              // hw_device_ctx 모드에서는 인코더가 시스템 메모리(RAM)의 NV12 프레임을
              // 직접 받아서 내부적으로 GPU로 업로드합니다.
              // hw_frames_ctx보다 약간 느리지만 호환성이 훨씬 좋습니다.
              //
              // pix_fmt을 NV12로 변경: 하드웨어 서피스(QSV) 대신 소프트웨어 포맷 직접 사용.
              // _isQsvEncoder = false: WriteFrame()에서 수동 업로드를 건너뜁니다.
              if (!framesCtxReady)
              {
                _pCodecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
                _pCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                _isQsvEncoder = false; // 시스템 메모리 모드이므로 프레임 업로드 불필요
              }

              // hwDeviceCtx의 소유권은 코덱 컨텍스트로 이전했으므로
              // 로컬 참조를 해제합니다 (참조 카운트 관리).
              // 참고: https://ffmpeg.org/doxygen/trunk/group__lavu__buffer.html
              ffmpeg.av_buffer_unref(&hwDeviceCtx);
            }

            // ── 인코더별 속도 프리셋 지정 ──
            // 프리셋은 인코딩 속도 ↔ 압축 효율 간의 트레이드오프를 결정합니다.
            // 빠른 프리셋: 인코딩 빠르지만 파일 크기 증가
            // 느린 프리셋: 인코딩 느리지만 같은 품질에서 파일 크기 감소
            //
            // 참고: https://ffmpeg.org/doxygen/trunk/group__opt__set__funcs.html
            if (encoderName.Contains("nvenc"))
            {
              // NVENC 프리셋 범위: p1(최고속) ~ p7(최고품질). p4 = 중간(default)
              ffmpeg.av_opt_set(_pCodecContext->priv_data, "preset", "p4", 0);
            }
            else if (encoderName.Contains("libx264") || encoderName.Contains("libx265"))
            {
              // x264/x265 프리셋: ultrafast, superfast, veryfast, faster, fast, medium, slow, ...
              // CPU 인코딩은 병목이 되기 쉬우므로 빠른 프리셋 사용
              ffmpeg.av_opt_set(_pCodecContext->priv_data, "preset", "veryfast", 0);
            }
            else if (encoderName == "libsvtav1")
            {
              // SVT-AV1 프리셋: 0(최고 품질/최저 속도) ~ 13(최고 속도/최저 품질)
              ffmpeg.av_opt_set(_pCodecContext->priv_data, "preset", "10", 0);
            }
            else if (encoderName == "libaom-av1")
            {
              // libaom cpu-used: 0(최고 품질) ~ 8(최고 속도)
              ffmpeg.av_opt_set(_pCodecContext->priv_data, "cpu-used", "6", 0);
              // "realtime" usage: 단일 패스, 저지연 인코딩 모드
              ffmpeg.av_opt_set(_pCodecContext->priv_data, "usage", "realtime", 0);
            }
            else if (encoderName == "libvvenc")
            {
              // libvvenc 프리셋: slower, slow, medium, fast, faster. 디폴트가 medium이므로 faster로 변경하여 성능 확보
              ffmpeg.av_opt_set(_pCodecContext->priv_data, "preset", "faster", 0);
            }
            else if (encoderName.Contains("prores"))
            {
              // ProRes는 프리셋 대신 프로파일로 품질/용량을 결정합니다.
              // standard(422): 편집용 표준 프로파일. proxy/lt(저용량) ~ hq/4444(고품질)도 가능.
              ffmpeg.av_opt_set(_pCodecContext->priv_data, "profile", "standard", 0);
            }
            // 다른 인코더들(AMF, QSV 등)은 기본 프리셋을 그대로 사용합니다.
            // 지원하지 않는 옵션을 설정하면 에러가 발생할 수 있으므로 건너뜁니다.

            // ── 인코더 열기 ──
            // avcodec_open2: 위에서 설정한 모든 파라미터를 기반으로 인코더를 초기화합니다.
            // 성공하면 0 이상을 반환하고, 이후 avcodec_send_frame/avcodec_receive_packet으로
            // 프레임 인코딩이 가능해집니다.
            // 참고: https://ffmpeg.org/doxygen/trunk/group__lavc__core.html
            RenderLog.Info(
              $"[인코더 진단] {encoderName}: {_pCodecContext->width}x{_pCodecContext->height}, pix_fmt={_pCodecContext->pix_fmt}, gop={_pCodecContext->gop_size}, max_b_frames={_pCodecContext->max_b_frames}, framerate={_pCodecContext->framerate.num}/{_pCodecContext->framerate.den}, thread_count={_pCodecContext->thread_count}, bit_rate={_pCodecContext->bit_rate}, rc_max_rate={_pCodecContext->rc_max_rate}, rc_buffer_size={_pCodecContext->rc_buffer_size}, mode={_settings.Mode}"
            );
            var openResult = ffmpeg.avcodec_open2(_pCodecContext, pCodec, null);
            if (openResult >= 0)
            {
              bool isHardware =
                encoderName.Contains("nvenc")
                || encoderName.Contains("amf")
                || encoderName.Contains("qsv")
                || encoderName.Contains("videotoolbox");
              string accelStatus = isHardware ? "하드웨어 가속 활성화" : "소프트웨어 CPU 사용";
              RenderLog.Info($"비디오 인코더 구동 성공: {encoderName} ({accelStatus})");
              _dstPixFmt = selectedPixFmt;
              SelectedEncoderName = encoderName;
              openedSuccessfully = true;
              break;
            }

            // 구동 실패 시 코덱 컨텍스트를 해제하고 다음 인코더를 시도합니다.
            RenderLog.Warn(
              $"{encoderName} 하드웨어 인코더 구동 실패: {GetFFmpegErrorString(openResult)} (Code: {openResult}). 다음 후보 코덱을 테스트합니다."
            );
            var pCodecCtx = _pCodecContext;
            ffmpeg.avcodec_free_context(&pCodecCtx);
            _pCodecContext = null;
          }

          if (!openedSuccessfully || _pCodecContext == null)
          {
            throw new Exception(
              "사용 가능한 비디오 인코더(하드웨어 가속 및 CPU 소프트웨어 전체)의 구동에 실패했습니다."
            );
          }

          // ── 4단계: 코덱 파라미터를 스트림에 복사 ──
          // 코덱 컨텍스트에 설정된 파라미터(해상도, 코덱 ID, 프로파일 등)를
          // 스트림의 codecpar에 복사합니다. 컨테이너 헤더에 기록될 정보입니다.
          // 참고: https://ffmpeg.org/doxygen/trunk/group__lavc__core.html
          if (ffmpeg.avcodec_parameters_from_context(_pVideoStream->codecpar, _pCodecContext) < 0)
            throw new Exception("코덱 파라미터를 스트림에 복사하는 데 실패했습니다.");

          // ── 4.5단계 (선택): 오디오 스트림 초기화 ──
          // EncoderSettings.Audio가 설정되어 있으면 오디오 스트림을 추가합니다.
          if (_settings.Audio.HasValue)
          {
            InitializeAudioStream(_settings.Audio.Value);
            if (_settings.ExtraAudioTracks != null)
            {
              foreach (var extraTrack in _settings.ExtraAudioTracks)
                InitializeAudioStream(extraTrack);
            }
          }

          // ── 5단계: 출력 파일 I/O 열기 ──
          // AVFMT_NOFILE 플래그가 없는 포맷은 실제 파일 I/O가 필요합니다.
          // avio_open으로 파일을 쓰기 모드로 열고, 포맷 컨텍스트의 pb(I/O 컨텍스트)에 연결합니다.
          // 참고: https://ffmpeg.org/doxygen/trunk/group__lavf__io.html
          if ((_pFormatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
          {
            if (ffmpeg.avio_open(&_pFormatContext->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE) < 0)
              throw new Exception($"출력 파일 {outputPath}를 열 수 없습니다.");
          }

          // 파일 헤더(ftyp, moov 등 컨테이너 메타데이터) 작성.
          // 이 시점 이후부터 av_interleaved_write_frame으로 패킷을 쓸 수 있습니다.
          // 참고: https://ffmpeg.org/doxygen/trunk/group__lavf__encoding.html
          if (ffmpeg.avformat_write_header(_pFormatContext, null) < 0)
            throw new Exception("파일 헤더를 작성하는 데 실패했습니다.");

          // ── 6단계: 프레임 버퍼 및 색공간 변환기 초기화 ──
          // av_frame_alloc: AVFrame 구조체를 힙에 할당합니다 (픽셀 데이터는 아직 없음).
          // 참고: https://ffmpeg.org/doxygen/trunk/group__lavu__frame.html
          _pSrcFrame = ffmpeg.av_frame_alloc();
          _pDstFrame = ffmpeg.av_frame_alloc();
          _pPacket = ffmpeg.av_packet_alloc();
          if (_pSrcFrame == null || _pDstFrame == null || _pPacket == null)
            throw new Exception("프레임 또는 패킷 구조체를 할당하지 못했습니다.");

          // 목적지 프레임의 형식과 크기를 설정한 뒤 실제 픽셀 버퍼를 할당합니다.
          // av_frame_get_buffer: format, width, height에 맞는 메모리를 할당하고
          // data[]/linesize[] 포인터를 설정합니다. 두 번째 인자 0은 기본 정렬을 의미합니다.
          _pDstFrame->format = (int)_dstPixFmt;
          _pDstFrame->width = _settings.Width;
          _pDstFrame->height = _settings.Height;
          _pDstFrame->color_range = AVColorRange.AVCOL_RANGE_JPEG;

          if (ffmpeg.av_frame_get_buffer(_pDstFrame, 0) < 0)
            throw new Exception($"{_dstPixFmt} 프레임 버퍼를 할당하는 데 실패했습니다.");

          // sws_getContext: 소프트웨어 스케일러(색공간 변환기) 초기화.
          // 입력: RGBA (Unity 캡처 포맷, 4바이트/픽셀)
          // 출력: YUV420P 또는 NV12 (인코더 요구 포맷)
          //
          // 여기서는 해상도 변환 없이 색공간만 변환하지만, sws_scale은
          // 동시에 리사이즈도 수행할 수 있습니다.
          //
          // SWS_POINT: 최근접 이웃 보간법. 해상도 변환 없이 색공간만 변환하므로
          // 보간 알고리즘이 출력 품질에 영향을 주지 않으며, SWS_POINT가 가장 빠릅니다.
          //
          // 참고: https://ffmpeg.org/doxygen/trunk/group__libsws.html
          _pSwsContext = ffmpeg.sws_getContext(
            _settings.Width,
            _settings.Height,
            RawSourceFormat,
            _settings.Width,
            _settings.Height,
            _dstPixFmt,
            (int)SwsFlags.SWS_POINT,
            null,
            null,
            null
          );

          if (_pSwsContext == null)
            throw new Exception("소프트웨어 스케일러를 초기화하는 데 실패했습니다.");

          _isInitialized = true;
          _loggedConversionMode = false;
        }
        catch (NotSupportedException ex)
        {
          Dispose();
          throw new Exception(
            "FFmpeg 네이티브 라이브러리(DLL)를 로드할 수 없습니다. UserLibs 폴더의 FFmpeg DLL 파일(avcodec, avformat 등)이 누락되었거나 손상되었는지 확인하고, UserLibs 폴더의 FFmpeg DLL 파일들을 삭제한 후 게임을 재시작하세요.",
            ex
          );
        }
        catch (Exception)
        {
          // 초기화 도중 실패 시 부분 할당된 리소스를 모두 정리합니다.
          Dispose();
          throw;
        }
      }
    }

    // ================================================================
    // 품질/비트레이트 설정
    // ================================================================

    /// <summary>
    /// 사용자가 선택한 비트레이트 제어 모드(CBR, VBR, CQP)에 따라
    /// 코덱 컨텍스트에 품질 관련 파라미터를 설정합니다.
    ///
    /// ── 비트레이트 제어 모드 설명 ──
    ///
    /// • CBR (Constant Bitrate): 일정한 비트레이트를 유지. 스트리밍에 적합.
    ///   → bit_rate = rc_max_rate = rc_min_rate (모두 동일)
    ///
    /// • VBR (Variable Bitrate): 장면 복잡도에 따라 비트레이트가 변동.
    ///   → bit_rate = 목표값, rc_max_rate = 최대 허용값
    ///
    /// • CQP (Constant Quantization Parameter): 고정 양자화 값 사용.
    ///   → 값이 낮을수록 고품질/대용량, 높을수록 저품질/소용량
    ///   → 각 인코더마다 설정 방식이 다름 (아래 참조)
    ///
    /// 참고 - 인코더별 CQP 옵션:
    ///   NVENC:     "qp" (단일 값으로 I/P/B 프레임 모두 적용)
    ///   AMF:       "qp_i" + "qp_p" (I프레임, P프레임 별도 설정)
    ///   QSV:       "global_quality" (Intel 고유 옵션)
    ///   소프트웨어: "crf" (Constant Rate Factor, 더 스마트한 품질 제어)
    ///
    /// 참고: https://ffmpeg.org/doxygen/trunk/structAVCodecContext.html
    /// </summary>
    private void ApplyQualitySettings(string encoderName)
    {
      // ProRes는 고정 품질 코덱으로 비트레이트 제어(CBR/VBR)를 지원하지 않습니다.
      // 품질은 프로파일(proxy/lt/standard/hq)로 결정되므로 CBR/VBR 설정은 무시합니다.
      if (encoderName.Contains("prores") && _settings.Mode != RateControlMode.CQP)
      {
        return;
      }

      switch (_settings.Mode)
      {
        case RateControlMode.CBR:
          // kbps → bps 변환 (FFmpeg의 bit_rate 필드는 bps 단위)
          _pCodecContext->bit_rate = _settings.TargetBitrate * 1000L;

          // libvvenc는 rc_min_rate/rc_max_rate/rc_buffer_size를 지원하지 않으므로
          // bit_rate만 설정하고 나머지는 내부 rate control에 위임합니다.
          if (encoderName != "libvvenc")
          {
            // CBR에서는 목표/최대/최소 비트레이트를 모두 동일하게 설정합니다.
            // rc_buffer_size는 비트레이트 버퍼링의 크기를 결정합니다.
            _pCodecContext->rc_max_rate = _pCodecContext->rc_min_rate = _settings.TargetBitrate * 1000L;
            _pCodecContext->rc_buffer_size = _settings.TargetBitrate * 1000;

            // nal-hrd=cbr: x264/x265 전용 옵션. NAL HRD(Hypothetical Reference Decoder) 규격에
            // 맞는 CBR 비트스트림을 생성합니다. 스트리밍 호환성을 위해 필요할 수 있습니다.
            if (encoderName.Contains("libx264") || encoderName.Contains("libx265"))
            {
              ffmpeg.av_opt_set(_pCodecContext->priv_data, "nal-hrd", "cbr", 0);
            }
          }
          break;

        case RateControlMode.VBR:
          // kbps → bps 변환
          _pCodecContext->bit_rate = _settings.TargetBitrate * 1000L;

          // libvvenc는 rc_max_rate/rc_buffer_size를 지원하지 않으므로 건너릴니다.
          if (encoderName != "libvvenc")
          {
            // MaxBitrate가 TargetBitrate보다 큰 경우 그대로 사용,
            // 그렇지 않으면 TargetBitrate의 1.5배를 최대값으로 설정합니다.
            var maxRate =
              _settings.MaxBitrate > _settings.TargetBitrate
                ? _settings.MaxBitrate * 1000L
                : _settings.TargetBitrate * 1500L;

            _pCodecContext->rc_max_rate = maxRate;
            // 버퍼 크기를 최대 비트레이트의 2배로 설정하여
            // 순간적인 비트레이트 스파이크를 수용합니다.
            // int 범위를 넘는 값은 잘려 음수가 되어 avcodec_open2가 실패하므로 클램프합니다.
            _pCodecContext->rc_buffer_size = (int)Math.Min(maxRate * 2, int.MaxValue);
          }
          break;

        case RateControlMode.CQP:
          // 기본값 18: H.264에서 "시각적으로 거의 무손실"에 가까운 품질.
          // 다른 코덱에서도 비슷한 수준의 품질을 제공합니다.
          var qpValue = _settings.QualityValue is >= 0 and <= 51 ? _settings.QualityValue : 18;

          if (encoderName.Contains("nvenc"))
          {
            // NVENC: "qp" 옵션 하나로 I/P/B 프레임 모두에 동일한 QP 적용
            ffmpeg.av_opt_set(_pCodecContext->priv_data, "qp", qpValue.ToString(), 0);
          }
          else if (encoderName.Contains("amf"))
          {
            // AMF: I프레임(qp_i)과 P프레임(qp_p)의 QP를 별도로 설정해야 합니다.
            // B-프레임은 비활성화했으므로 qp_b는 설정하지 않습니다.
            ffmpeg.av_opt_set(_pCodecContext->priv_data, "qp_i", qpValue.ToString(), 0);
            ffmpeg.av_opt_set(_pCodecContext->priv_data, "qp_p", qpValue.ToString(), 0);
          }
          else if (encoderName.Contains("qsv"))
          {
            // QSV: "global_quality" 옵션으로 ICQ(Intelligent Constant Quality) 모드 사용
            ffmpeg.av_opt_set(_pCodecContext->priv_data, "global_quality", qpValue.ToString(), 0);
          }
          else if (encoderName == "libvvenc")
          {
            // libvvenc: "qp" 옵션으로 Constant QP 모드 지정
            ffmpeg.av_opt_set(_pCodecContext->priv_data, "qp", qpValue.ToString(), 0);
          }
          else if (encoderName == "prores_ks")
          {
            // prores_ks: qscale(2=최고품질 ~ 31=최저품질)로 품질 제어.
            // QP(0~51)를 qscale(2~31) 범위로 선형 매핑합니다.
            var qscale = 2 + qpValue * 29 / 51;
            _pCodecContext->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            _pCodecContext->global_quality = qscale * ffmpeg.FF_QP2LAMBDA;
          }
          else if (encoderName.Contains("videotoolbox"))
          {
            // VideoToolbox: QP 개념 대신 0~100 스케일의 품질 값(kVTCompressionPropertyKey_Quality)을
            // 사용합니다. AV_CODEC_FLAG_QSCALE 플래그와 global_quality(0~10000, 값/100 = 품질%)로 전달합니다.
            // QP(0=최고품질, 51=최저품질)를 품질%(100=최고)로 역매핑합니다.
            // 참고: Apple Silicon에서만 지원되며, 미지원 환경에서는 인코더 초기화가 실패하고
            //       다음 후보(libx264 등)로 자동 폴백됩니다.
            var quality = (51 - qpValue) * 100 / 51; // 0~100
            _pCodecContext->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            _pCodecContext->global_quality = quality * ffmpeg.FF_QP2LAMBDA;
          }
          else // 소프트웨어 인코더 (libx264, libx265, libvpx-vp9 등)
          {
            // CRF(Constant Rate Factor): QP보다 더 스마트한 품질 제어.
            // 장면 복잡도에 따라 비트레이트를 자동 조절하여 일정한 시각적 품질을 유지합니다.
            // 값 범위: 0(무손실) ~ 51(최저품질). 일반적으로 18~28 사이를 사용합니다.
            ffmpeg.av_opt_set(_pCodecContext->priv_data, "crf", qpValue.ToString(), 0);
          }
          break;
        default:
          throw new ArgumentOutOfRangeException();
      }
    }

    // ================================================================
    // 프레임 쓰기 (Write Frame)
    // ================================================================

    /// <summary>
    /// Unity에서 캡처한 RGBA 픽셀 데이터(byte* 포인터)를 받아 한 프레임을 인코딩합니다.
    ///
    /// Unity의 텍스처는 Bottom-Left origin(화면 좌하단이 (0,0))이지만,
    /// 비디오 표준은 Top-Left origin(화면 좌상단이 (0,0))을 사용합니다.
    /// 이를 해결하기 위해 "음수 stride 트릭"을 사용합니다:
    ///
    /// ── 음수 stride(linesize) 트릭 ──
    ///   • data[0]을 버퍼의 마지막 행(맨 아래줄) 시작 위치로 설정
    ///   • linesize[0]을 -stride(음수)로 설정
    ///   → sws_scale이 행을 역순으로 읽어 자연스럽게 상하 반전(vflip) 수행
    ///   → 추가 메모리 복사나 별도의 반전 연산이 필요 없어 매우 효율적
    ///
    /// stride = Width × 4 (RGBA는 픽셀당 4바이트)
    /// </summary>
    /// <param name="buffer">Unity RGBA 픽셀 데이터의 시작 주소</param>
    public void WriteFrameFromBuffer(byte* buffer)
    {
      lock (_lock)
      {
        if (_disposed || !_isInitialized)
          return;

        var stride = _settings.Width * 4; // RGBA = 4 bytes per pixel
        _pSrcFrame->data[0] = buffer + stride * (_settings.Height - 1); // 마지막 행의 시작 주소
        _pSrcFrame->linesize[0] = -stride; // 음수 stride로 상하 반전
        _pSrcFrame->width = _settings.Width;
        _pSrcFrame->height = _settings.Height;
        _pSrcFrame->format = (int)RawSourceFormat;

        WriteFrame();
      }
    }

    /// <summary>
    /// Unity NativeArray&lt;byte&gt;에서 읽기 전용 포인터를 얻어 프레임을 인코딩합니다.
    /// NativeArray는 Unity의 네이티브 메모리 할당자로 관리되는 배열로,
    /// GC(가비지 컬렉터) 압력 없이 대량의 데이터를 다룰 수 있습니다.
    /// </summary>
    public void WriteFrameFromNativeBuffer(NativeArray<byte> buffer)
    {
      lock (_lock)
      {
        if (_disposed || !_isInitialized)
          return;
        WriteFrameFromBuffer((byte*)buffer.GetUnsafeReadOnlyPtr());
      }
    }

    /// <summary>
    /// IntPtr(네이티브 포인터)로부터 프레임을 인코딩합니다.
    /// 외부 네이티브 플러그인이나 커스텀 캡처 소스에서 사용할 수 있습니다.
    /// </summary>
    public void SendDataFromNativeBuffer(IntPtr pointer, int length)
    {
      lock (_lock)
      {
        if (_disposed || !_isInitialized)
          return;
        var preConvertedSize = _settings.Width * _settings.Height * 3 / 2;

        if (!_loggedConversionMode)
        {
          _loggedConversionMode = true;
          RenderLog.Info(
            length == preConvertedSize
              ? "[인코더] 고속 GPU 컬러 변환 활성화 (NV12/YUV420P Direct Write, sws_scale 우회)"
              : "[인코더] CPU 컬러 변환 폴백 활성화 (RGBA Input, sws_scale 사용)"
          );
        }

        if (length == preConvertedSize)
        {
          WritePreConvertedFrame((byte*)pointer.ToPointer());
        }
        else
        {
          WriteFrameFromBuffer((byte*)pointer.ToPointer());
        }
      }
    }

    /// <summary>
    /// 매니지드 byte[]에서 프레임을 인코딩합니다.
    /// fixed 키워드로 GC가 배열을 이동하지 못하도록 고정(pin)한 뒤 포인터를 전달합니다.
    /// </summary>
    public void SendDataFromBuffer(byte[] buffer, int length)
    {
      lock (_lock)
      {
        if (_disposed || !_isInitialized)
          return;
        fixed (byte* pBuffer = buffer)
        {
          var preConvertedSize = _settings.Width * _settings.Height * 3 / 2;

          if (!_loggedConversionMode)
          {
            _loggedConversionMode = true;
            RenderLog.Info(
              length == preConvertedSize
                ? "[인코더] 고속 GPU 컬러 변환 활성화 (NV12/YUV420P Direct Write, sws_scale 우회)"
                : "[인코더] CPU 컬러 변환 폴백 활성화 (RGBA Input, sws_scale 사용)"
            );
          }

          if (length == preConvertedSize)
          {
            WritePreConvertedFrame(pBuffer);
          }
          else
          {
            WriteFrameFromBuffer(pBuffer);
          }
        }
      }
    }

    /// <summary>
    /// 이미 NV12 혹은 YUV420P로 변환된 프레임 데이터를 받아서 다이렉트로 복사하여 인코딩합니다.
    /// </summary>
    private void WritePreConvertedFrame(byte* buffer)
    {
      // 실패 시 인코더가 아직 참조 중인 버퍼에 쓰게 되어 프레임 깨짐/크래시가 발생하므로 반드시 확인합니다.
      var writableResult = ffmpeg.av_frame_make_writable(_pDstFrame);
      if (writableResult < 0)
        throw new Exception(
          $"프레임 버퍼를 쓰기 가능 상태로 만들지 못했습니다: {GetFFmpegErrorString(writableResult)}"
        );

      var width = _settings.Width;
      var height = _settings.Height;

      switch (_dstPixFmt)
      {
        case AVPixelFormat.AV_PIX_FMT_NV12 or AVPixelFormat.AV_PIX_FMT_QSV:
        {
          // Y Plane 복사
          var pSrcY = buffer;
          var pDstY = _pDstFrame->data[0];
          var dstStrideY = _pDstFrame->linesize[0];

          if (width == dstStrideY)
          {
            Buffer.MemoryCopy(pSrcY, pDstY, dstStrideY * height, width * height);
          }
          else
          {
            for (var y = 0; y < height; y++)
            {
              Buffer.MemoryCopy(pSrcY + y * width, pDstY + y * dstStrideY, width, width);
            }
          }

          // UV Plane 복사 (Y 평면 크기 이후 위치)
          var pSrcUV = buffer + width * height;
          var pDstUV = _pDstFrame->data[1];
          var dstStrideUV = _pDstFrame->linesize[1];
          var uvHeight = height / 2;

          if (width == dstStrideUV)
          {
            Buffer.MemoryCopy(pSrcUV, pDstUV, dstStrideUV * uvHeight, width * uvHeight);
          }
          else
          {
            for (var y = 0; y < uvHeight; y++)
            {
              Buffer.MemoryCopy(pSrcUV + y * width, pDstUV + y * dstStrideUV, width, width);
            }
          }

          break;
        }
        case AVPixelFormat.AV_PIX_FMT_YUV420P:
        {
          // Y Plane 복사
          var pSrcY = buffer;
          var pDstY = _pDstFrame->data[0];
          var dstStrideY = _pDstFrame->linesize[0];

          if (width == dstStrideY)
          {
            Buffer.MemoryCopy(pSrcY, pDstY, dstStrideY * height, width * height);
          }
          else
          {
            for (var y = 0; y < height; y++)
            {
              Buffer.MemoryCopy(pSrcY + y * width, pDstY + y * dstStrideY, width, width);
            }
          }

          // U Plane 복사
          var pSrcU = buffer + width * height;
          var pDstU = _pDstFrame->data[1];
          var dstStrideU = _pDstFrame->linesize[1];
          var chromaWidth = width / 2;
          var chromaHeight = height / 2;

          if (chromaWidth == dstStrideU)
          {
            Buffer.MemoryCopy(pSrcU, pDstU, dstStrideU * chromaHeight, chromaWidth * chromaHeight);
          }
          else
          {
            for (var y = 0; y < chromaHeight; y++)
            {
              Buffer.MemoryCopy(pSrcU + y * chromaWidth, pDstU + y * dstStrideU, chromaWidth, chromaWidth);
            }
          }

          // V Plane 복사
          var pSrcV = pSrcU + chromaWidth * chromaHeight;
          var pDstV = _pDstFrame->data[2];
          var dstStrideV = _pDstFrame->linesize[2];

          if (chromaWidth == dstStrideV)
          {
            Buffer.MemoryCopy(pSrcV, pDstV, dstStrideV * chromaHeight, chromaWidth * chromaHeight);
          }
          else
          {
            for (var y = 0; y < chromaHeight; y++)
            {
              Buffer.MemoryCopy(pSrcV + y * chromaWidth, pDstV + y * dstStrideV, chromaWidth, chromaWidth);
            }
          }

          break;
        }
        default:
          throw new NotSupportedException(
            $"이 픽셀 포맷({_dstPixFmt})은 사전 변환된 버퍼 다이렉트 복사를 지원하지 않습니다."
          );
      }

      // QSV 가속 처리 또는 인코더 전송
      if (_isQsvEncoder)
      {
        ffmpeg.av_frame_unref(_pHwFrame);
        try
        {
          var ret = ffmpeg.av_hwframe_get_buffer(_pCodecContext->hw_frames_ctx, _pHwFrame, 0);
          if (ret < 0)
            throw new Exception($"QSV 하드웨어 프레임 버퍼 할당 실패 — {GetFFmpegErrorString(ret)} (Code: {ret})");

          ret = ffmpeg.av_hwframe_transfer_data(_pHwFrame, _pDstFrame, 0);
          if (ret < 0)
            throw new Exception($"QSV 프레임 업로드 실패 — {GetFFmpegErrorString(ret)} (Code: {ret})");

          _pHwFrame->pts = _frameIndex++;
          SendFrameAndWritePackets(_pHwFrame);
        }
        finally
        {
          ffmpeg.av_frame_unref(_pHwFrame);
        }
      }
      else
      {
        _pDstFrame->pts = _frameIndex++;
        SendFrameAndWritePackets(_pDstFrame);
      }
    }

    /// <summary>
    /// 실제 프레임 인코딩의 핵심 로직.
    ///
    /// 처리 흐름:
    ///   1. av_frame_make_writable: 목적지 프레임의 버퍼가 쓰기 가능한지 확인
    ///      (인코더가 이전 프레임의 버퍼를 아직 참조 중이면 새 버퍼를 할당)
    ///   2. sws_scale: RGBA → YUV420P/NV12 색공간 변환
    ///   3. (QSV hw_frames 모드) av_hwframe_transfer_data: NV12 → QSV 하드웨어 서피스 업로드
    ///   4. PTS 설정 후 인코더에 전달
    ///
    /// 참고: https://ffmpeg.org/doxygen/trunk/group__lavu__frame.html (av_frame_make_writable)
    /// 참고: https://ffmpeg.org/doxygen/trunk/group__libsws.html (sws_scale)
    /// </summary>
    private void WriteFrame()
    {
      // 인코더가 이전 프레임의 버퍼를 참조 중일 수 있으므로
      // 쓰기 가능한 상태를 보장합니다. 참조가 걸려 있으면 내부적으로 새 버퍼를 할당합니다.
      var writableResult = ffmpeg.av_frame_make_writable(_pDstFrame);
      if (writableResult < 0)
        throw new Exception(
          $"프레임 버퍼를 쓰기 가능 상태로 만들지 못했습니다: {GetFFmpegErrorString(writableResult)}"
        );

      // ── 색공간 변환: RGBA → YUV420P 또는 NV12 ──
      // sws_scale 파라미터 설명:
      //   _pSwsContext:              사전 초기화된 변환 컨텍스트
      //   _pSrcFrame->data:          입력 데이터 평면 배열 (RGBA는 data[0]만 사용)
      //   _pSrcFrame->linesize:      입력의 각 평면 stride (바이트/행)
      //   0:                         처리 시작할 소스의 첫 번째 행 인덱스
      //   _settings.Height:          처리할 행 수 (= 전체 높이)
      //   _pDstFrame->data:          출력 데이터 평면 배열
      //   _pDstFrame->linesize:      출력의 각 평면 stride
      ffmpeg.sws_scale(
        _pSwsContext,
        _pSrcFrame->data,
        _pSrcFrame->linesize,
        0,
        _settings.Height,
        _pDstFrame->data,
        _pDstFrame->linesize
      );

      if (_isQsvEncoder)
      {
        // ── QSV hw_frames_ctx 모드: 시스템 메모리 → GPU 서피스 업로드 ──
        //
        // av_hwframe_get_buffer: hw_frames_ctx의 프레임 풀에서 빈 하드웨어 프레임을 가져옵니다.
        // av_hwframe_transfer_data: NV12 소프트웨어 프레임의 데이터를 QSV 서피스로 복사(업로드)합니다.
        //
        // 이 과정을 거치면 인코더가 GPU 메모리에서 직접 프레임을 읽어
        // PCIe 대역폭 사용을 최소화할 수 있습니다.
        //
        // 참고: https://ffmpeg.org/doxygen/trunk/group__lavu__hwcontext.html

        ffmpeg.av_frame_unref(_pHwFrame);
        try
        {
          var ret = ffmpeg.av_hwframe_get_buffer(_pCodecContext->hw_frames_ctx, _pHwFrame, 0);
          if (ret < 0)
            throw new Exception($"QSV 하드웨어 프레임 버퍼 할당 실패 — {GetFFmpegErrorString(ret)} (Code: {ret})");

          ret = ffmpeg.av_hwframe_transfer_data(_pHwFrame, _pDstFrame, 0);
          if (ret < 0)
            throw new Exception($"QSV 프레임 업로드 실패 — {GetFFmpegErrorString(ret)} (Code: {ret})");

          // PTS(Presentation Time Stamp): 이 프레임이 표시되어야 하는 시점.
          // _frameIndex를 1씩 증가시키며, 실제 시간은 PTS × time_base로 계산됩니다.
          _pHwFrame->pts = _frameIndex++;
          SendFrameAndWritePackets(_pHwFrame);
        }
        finally
        {
          // 사용이 끝났으므로 즉시 참조 해제하여 풀로 반환될 수 있게 합니다.
          ffmpeg.av_frame_unref(_pHwFrame);
        }
      }
      else
      {
        // ── 일반 인코더 (소프트웨어 또는 hw_device_ctx 모드) ──
        // 소프트웨어 프레임을 직접 인코더에 전달합니다.
        // hw_device_ctx 모인 경우, 인코더가 내부적으로 GPU 업로드를 처리합니다.
        _pDstFrame->pts = _frameIndex++;
        SendFrameAndWritePackets(_pDstFrame);
      }
    }

    // ================================================================
    // 인코더 Send/Receive 및 패킷 쓰기
    // ================================================================

    /// <summary>
    /// FFmpeg의 Send/Receive 패턴으로 프레임을 인코딩하고 결과 패킷을 파일에 기록합니다.
    ///
    /// ── Send/Receive API 설명 ──
    /// FFmpeg 3.x부터 도입된 비동기 인코딩 API입니다:
    ///
    ///   avcodec_send_frame(ctx, frame)
    ///     → 인코더의 내부 큐에 프레임을 넣습니다.
    ///     → 인코더가 충분한 프레임을 모으면 인코딩을 시작합니다.
    ///
    ///   avcodec_receive_packet(ctx, packet)
    ///     → 인코딩이 완료된 패킷(압축된 데이터)을 꺼냅니다.
    ///     → EAGAIN: 아직 출력이 준비되지 않음 (더 많은 프레임 필요)
    ///     → EOF: 인코딩 완료 (flush 시)
    ///
    /// 하나의 send_frame 호출에 대해 0개 이상의 패킷이 나올 수 있으므로
    /// receive_packet을 EAGAIN/EOF가 나올 때까지 반복합니다.
    ///
    /// ── 패킷 타임스탬프 처리 ──
    /// av_packet_rescale_ts: 코덱의 time_base에서 스트림의 time_base로 PTS/DTS를 변환합니다.
    /// 코덱과 컨테이너가 서로 다른 time_base를 사용할 수 있기 때문에 반드시 필요합니다.
    /// 예) 코덱 time_base=1/60, 스트림 time_base=1/90000 → PTS 150 → PTS 225000
    ///
    /// 참고:
    ///   Send/Receive API: https://ffmpeg.org/doxygen/trunk/group__lavc__encdec.html
    ///   패킷: https://ffmpeg.org/doxygen/trunk/group__lavc__packet.html
    ///   인터리브 쓰기: https://ffmpeg.org/doxygen/trunk/group__lavf__encoding.html
    /// </summary>
    /// <param name="pFrame">인코딩할 프레임. null을 전달하면 인코더 플러시(지연 프레임 출력).</param>
    private void SendFrameAndWritePackets(AVFrame* pFrame)
    {
      // 인코더 내부 큐에 프레임 전달
      var response = ffmpeg.avcodec_send_frame(_pCodecContext, pFrame);
      if (response < 0)
      {
        throw new Exception($"인코더에 프레임을 전송하는 도중 에러가 발생했습니다. (Code: {response})");
      }

      // 인코딩 완료된 패킷을 모두 꺼내어 파일에 기록합니다.
      try
      {
        while (true)
        {
          response = ffmpeg.avcodec_receive_packet(_pCodecContext, _pPacket);

          // EAGAIN: 인코더가 더 많은 입력 프레임을 필요로 함 → 루프 종료
          // EOF:    인코더 플러시 완료 (모든 지연 프레임 출력됨) → 루프 종료
          if (response == ffmpeg.AVERROR(ffmpeg.EAGAIN) || response == ffmpeg.AVERROR_EOF)
            break;
          if (response < 0)
            throw new Exception($"인코더에서 패킷을 수신하는 도중 에러가 발생했습니다. (Code: {response})");

          // 코덱 타임베이스 → 스트림 타임베이스로 PTS/DTS 변환
          ffmpeg.av_packet_rescale_ts(_pPacket, _pCodecContext->time_base, _pVideoStream->time_base);
          _pPacket->stream_index = _pVideoStream->index;

          // av_interleaved_write_frame: 패킷을 컨테이너에 기록합니다.
          // "interleaved"는 여러 스트림(비디오+오디오)의 패킷을 시간 순서대로
          // 인터리빙하여 쓴다는 의미입니다. 비디오만 있어도 이 함수를 사용합니다.
          response = ffmpeg.av_interleaved_write_frame(_pFormatContext, _pPacket);
          if (response < 0)
            throw new Exception($"패킷을 파일에 쓰는 도중 에러가 발생했습니다. (Code: {response})");

          // 패킷의 데이터 참조를 해제하여 다음 receive_packet에서 재사용할 수 있게 합니다.
          ffmpeg.av_packet_unref(_pPacket);
        }
      }
      finally
      {
        // 비정상적인 탈출(예외 발생) 등에도 안전하게 패킷 참조 해제
        ffmpeg.av_packet_unref(_pPacket);
      }
    }

    // ================================================================
    // 인코딩 종료 (End)
    // ================================================================

    /// <summary>
    /// 인코딩을 완료합니다.
    ///
    /// 대부분의 인코더는 내부 버퍼에 프레임을 쌓아두고(delayed frames/지연 프레임),
    /// 충분한 프레임이 모였을 때 인코딩을 수행합니다. End()에서는:
    ///
    ///   1. null 프레임을 보내 인코더를 "플러시 모드"로 전환합니다.
    ///      → 인코더가 내부 버퍼에 남은 모든 프레임을 인코딩하여 출력합니다.
    ///      → receive_packet이 EOF를 반환할 때까지 패킷을 꺼냅니다.
    ///
    ///   2. av_write_trailer: 컨테이너의 트레일러(파일 끝 메타데이터)를 작성합니다.
    ///      MP4의 경우 moov atom(인덱스 정보)이 여기서 완성됩니다.
    ///      ⚠️ 트레일러를 쓰지 않으면 파일이 손상되어 재생이 불가능합니다!
    ///
    /// 참고: https://ffmpeg.org/doxygen/trunk/group__lavf__encoding.html
    /// </summary>
    public void End()
    {
      lock (_lock)
      {
        if (_disposed || !_isInitialized)
          return;

        // 1. 비디오 인코더의 지연된 프레임 플러시 (null 프레임 전송으로 인코더에 "입력 끝" 신호)
        try
        {
          SendFrameAndWritePackets(null);
        }
        catch (Exception ex)
        {
          // 플러시 과정 중 발생한 에러 기록
          RenderLog.Error($"비디오 인코더 플러시 중 오류 발생: {ex.Message}");
        }

        // 1.5. 오디오 FIFO에 남은 샘플 플러시 + 오디오 인코더 플러시
        if (_audioEnabled)
        {
          try
          {
            FlushAudioEncoder();
          }
          catch (Exception ex)
          {
            RenderLog.Error($"오디오 인코더 플러시 중 오류 발생: {ex.Message}");
          }
        }

        // 2. 컨테이너 트레일러 작성 (moov atom 등 파일 종료 메타데이터)
        if (_pFormatContext != null)
        {
          ffmpeg.av_write_trailer(_pFormatContext);
        }

        _isInitialized = false;
      }
    }

    // ================================================================
    // 리소스 해제 (Dispose)
    // ================================================================

    /// <summary>
    /// 실제 리소스 해제 로직. 해제 순서가 중요합니다:
    ///
    ///   1. End() 호출 (아직 안 했다면) → 지연 프레임 플러시 + 트레일러 작성
    ///   2. SwsContext (스케일러) → 독립적, 먼저 해제 가능
    ///   3. AVFrame (소스/목적지) → 코덱이 참조할 수 있으므로 코덱보다 먼저 해제
    ///   4. AVCodecContext (코덱) → hw_frames_ctx/hw_device_ctx도 함께 해제됨
    ///   5. AVFormatContext (포맷/I/O) → 파일 핸들 닫기 포함
    ///
    /// 참고:
    ///   av_frame_free: https://ffmpeg.org/doxygen/trunk/group__lavu__frame.html
    ///   avcodec_free_context: https://ffmpeg.org/doxygen/trunk/group__lavc__core.html
    ///   avformat_free_context: https://ffmpeg.org/doxygen/trunk/group__lavf__core.html
    ///   avio_closep: https://ffmpeg.org/doxygen/trunk/group__lavf__io.html
    /// </summary>
    public void Dispose()
    {
      lock (_lock)
      {
        if (_disposed)
          return;

        // 만약 End()를 호출하지 않고 Dispose가 되는 경우도 있으므로 안전하게 End() 트리거.
        // 주의: _disposed를 먼저 세우면 End()가 첫 줄 가드에서 즉시 반환하여
        // 지연 프레임 플러시와 av_write_trailer(moov atom 기록)가 누락되어
        // 재생 불가능한 mp4가 생성됩니다. 반드시 End() 이후에 _disposed를 설정합니다.
        if (_isInitialized)
        {
          try
          {
            End();
          }
          catch
          { /* 무시 */
          }
        }
        _disposed = true;

        // Sws 스케일러 해제
        if (_pSwsContext != null)
        {
          ffmpeg.sws_freeContext(_pSwsContext);
          _pSwsContext = null;
        }

        // 소스 프레임 메모리 해제 (Unity RGBA 데이터를 참조하는 프레임)
        if (_pSrcFrame != null)
        {
          var pFrame = _pSrcFrame;
          ffmpeg.av_frame_free(&pFrame);
          _pSrcFrame = null;
        }

        // 목적지 프레임 메모리 해제 (YUV420P/NV12 변환 결과 프레임)
        if (_pDstFrame != null)
        {
          var pFrame = _pDstFrame;
          ffmpeg.av_frame_free(&pFrame);
          _pDstFrame = null;
        }

        // QSV 하드웨어 프레임 메모리 해제
        if (_pHwFrame != null)
        {
          var pFrame = _pHwFrame;
          ffmpeg.av_frame_free(&pFrame);
          _pHwFrame = null;
        }

        // 패킷 메모리 해제
        if (_pPacket != null)
        {
          var pPacket = _pPacket;
          ffmpeg.av_packet_free(&pPacket);
          _pPacket = null;
        }

        // 코덱 컨텍스트 해제
        // 이 함수는 내부적으로 hw_frames_ctx와 hw_device_ctx도 함께 해제합니다.
        if (_pCodecContext != null)
        {
          var pCodecCtx = _pCodecContext;
          ffmpeg.avcodec_free_context(&pCodecCtx);
          _pCodecContext = null;
        }

        // ── 오디오 관련 리소스 해제 ──
        DisposeAudioResources();

        // 포맷 컨텍스트 및 I/O 닫기
        if (_pFormatContext != null)
        {
          // AVFMT_NOFILE이 아닌 경우 avio를 수동으로 닫아야 합니다.
          // (avio_open으로 열었으므로 avio_closep로 닫음)
          if ((_pFormatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
          {
            var pPb = _pFormatContext->pb;
            if (pPb != null)
            {
              ffmpeg.avio_closep(&pPb);
              _pFormatContext->pb = null;
            }
          }
          // 포맷 컨텍스트 해제 (내부의 스트림 등도 함께 해제됨)
          ffmpeg.avformat_free_context(_pFormatContext);
          _pFormatContext = null;
        }

        GC.SuppressFinalize(this);
      }
    }

    /// <summary>
    /// 파이널라이저(소멸자). Dispose()가 호출되지 않은 채로 GC에 의해 수집될 때 실행됩니다.
    /// 네이티브 리소스 누수를 방지하는 안전장치입니다.
    /// ⚠️ 파이널라이저는 실행 시점이 보장되지 않으므로, 반드시 using 문이나
    ///    명시적 Dispose() 호출로 리소스를 관리하세요.
    /// </summary>
    ~FFmpegNativeEncoder()
    {
      Dispose();
    }

    // ================================================================
    // 오디오 인코딩 (Audio Encoding)
    // ================================================================

    /// <summary>
    /// Initializes one audio stream: codec context, container stream, resampler, FIFO and frame.
    /// The container can carry several — the render writes a default full mix plus per-source
    /// stems (music, hit sounds, microphone) so editors can rebalance without re-rendering.
    /// </summary>
    private AudioTrackState InitializeAudioStream(AudioSettings audioSettings)
    {
      var audioCodecId = audioSettings.AudioCodecId != 0 ? audioSettings.AudioCodecId : AVCodecID.AV_CODEC_ID_AAC;

      var pAudioCodec = ffmpeg.avcodec_find_encoder(audioCodecId);
      if (pAudioCodec == null)
        throw new Exception($"오디오 인코더를 찾을 수 없습니다: {audioCodecId}");

      var track = new AudioTrackState();
      track.CodecContext = ffmpeg.avcodec_alloc_context3(pAudioCodec);
      if (track.CodecContext == null)
        throw new Exception("오디오 코덱 컨텍스트를 할당하지 못했습니다.");

      var sampleRate = audioSettings.SampleRate > 0 ? audioSettings.SampleRate : 44100;
      var channels = audioSettings.Channels > 0 ? audioSettings.Channels : 2;
      var bitrate = audioSettings.Bitrate > 0 ? audioSettings.Bitrate : 128000;

#pragma warning disable CS0618
      var encoderSampleFmt =
        pAudioCodec->sample_fmts != null ? pAudioCodec->sample_fmts[0] : AVSampleFormat.AV_SAMPLE_FMT_FLTP;
#pragma warning restore CS0618

      track.CodecContext->sample_rate = sampleRate;
      track.CodecContext->sample_fmt = encoderSampleFmt;
      track.CodecContext->bit_rate = bitrate;
      track.CodecContext->strict_std_compliance = ffmpeg.FF_COMPLIANCE_EXPERIMENTAL;
      ffmpeg.av_channel_layout_default(&track.CodecContext->ch_layout, channels);
      track.CodecContext->time_base = new AVRational { num = 1, den = sampleRate };

      if ((_pFormatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
        track.CodecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

      var ret = ffmpeg.avcodec_open2(track.CodecContext, pAudioCodec, null);
      if (ret < 0)
        throw new Exception($"오디오 인코더 구동 실패: {GetFFmpegErrorString(ret)} (Code: {ret})");

      RenderLog.Info(
        $"오디오 인코더 구동 성공: {Marshal.PtrToStringAnsi((IntPtr)pAudioCodec->name)} (track={audioSettings.Title ?? "default"}, sample_rate={sampleRate}, channels={channels}, fmt={encoderSampleFmt})"
      );

      track.Stream = ffmpeg.avformat_new_stream(_pFormatContext, null);
      if (track.Stream == null)
        throw new Exception("오디오 스트림을 생성하지 못했습니다.");

      if (ffmpeg.avcodec_parameters_from_context(track.Stream->codecpar, track.CodecContext) < 0)
        throw new Exception("오디오 코덱 파라미터를 스트림에 복사하는 데 실패했습니다.");

      track.Stream->time_base = track.CodecContext->time_base;

      // Track identity: the title is what editors show per stream, and the default disposition is
      // what ordinary players pick for playback.
      if (!string.IsNullOrEmpty(audioSettings.Title))
      {
        ffmpeg.av_dict_set(&track.Stream->metadata, "title", audioSettings.Title, 0);
        ffmpeg.av_dict_set(&track.Stream->metadata, "handler_name", audioSettings.Title, 0);
      }
      if (audioSettings.IsDefaultTrack)
        track.Stream->disposition |= ffmpeg.AV_DISPOSITION_DEFAULT;

      var inputSampleFmt =
        audioSettings.InputSampleFormat != 0 ? audioSettings.InputSampleFormat : AVSampleFormat.AV_SAMPLE_FMT_FLT;

      SwrContext* swr = null;
      AVChannelLayout inLayout;
      ffmpeg.av_channel_layout_default(&inLayout, channels);
      ret = ffmpeg.swr_alloc_set_opts2(
        &swr,
        &track.CodecContext->ch_layout,
        encoderSampleFmt,
        sampleRate,
        &inLayout,
        inputSampleFmt,
        sampleRate,
        0,
        null
      );
      if (ret < 0 || swr == null)
        throw new Exception($"오디오 리샘플러 옵션 설정 실패: {GetFFmpegErrorString(ret)} (Code: {ret})");
      track.Swr = swr;

      ret = ffmpeg.swr_init(track.Swr);
      if (ret < 0)
        throw new Exception($"오디오 리샘플러 초기화 실패: {GetFFmpegErrorString(ret)} (Code: {ret})");

      track.Fifo = ffmpeg.av_audio_fifo_alloc(encoderSampleFmt, channels, 1);
      if (track.Fifo == null)
        throw new Exception("오디오 FIFO 버퍼를 할당하지 못했습니다.");

      track.Frame = ffmpeg.av_frame_alloc();
      if (_pAudioPacket == null)
        _pAudioPacket = ffmpeg.av_packet_alloc();
      if (track.Frame == null || _pAudioPacket == null)
        throw new Exception("오디오 프레임 또는 패킷 구조체를 할당하지 못했습니다.");

      var frameSize = track.CodecContext->frame_size > 0 ? track.CodecContext->frame_size : 1024;
      track.Frame->nb_samples = frameSize;
      track.Frame->format = (int)encoderSampleFmt;
      ffmpeg.av_channel_layout_copy(&track.Frame->ch_layout, &track.CodecContext->ch_layout);
      track.Frame->sample_rate = sampleRate;

      ret = ffmpeg.av_frame_get_buffer(track.Frame, 0);
      if (ret < 0)
        throw new Exception($"오디오 프레임 버퍼 할당 실패: {GetFFmpegErrorString(ret)} (Code: {ret})");

      track.SampleIndex = 0;
      _audioTracks.Add(track);
      _audioEnabled = true;
      return track;
    }

    /// <summary>인터리브드 float PCM을 지정한 트랙에 인코딩합니다.</summary>
    public void WriteAudioSamples(int trackIndex, float[] samples, int numSamples)
    {
      lock (_lock)
      {
        if (_disposed || !_isInitialized || !_audioEnabled)
          return;
        if (trackIndex < 0 || trackIndex >= _audioTracks.Count)
          return;

        fixed (float* pSamples = samples)
        {
          WriteAudioSamplesInternal(_audioTracks[trackIndex], (byte*)pSamples, numSamples);
        }
      }
    }

    public void WriteAudioSamples(float[] samples, int numSamples) => WriteAudioSamples(0, samples, numSamples);

    public void WriteAudioSamples(IntPtr samples, int numSamples)
    {
      lock (_lock)
      {
        if (_disposed || !_isInitialized || !_audioEnabled || _audioTracks.Count == 0)
          return;
        WriteAudioSamplesInternal(_audioTracks[0], (byte*)samples.ToPointer(), numSamples);
      }
    }

    /// <summary>
    /// 오디오 샘플 인코딩의 내부 구현: swr_convert → FIFO → frame_size 단위 인코딩.
    /// </summary>
    private void WriteAudioSamplesInternal(AudioTrackState track, byte* inputBuffer, int numSamples)
    {
      var pConvertedFrame = ffmpeg.av_frame_alloc();
      if (pConvertedFrame == null)
        return;

      try
      {
        pConvertedFrame->nb_samples = numSamples;
        pConvertedFrame->format = (int)track.CodecContext->sample_fmt;
        ffmpeg.av_channel_layout_copy(&pConvertedFrame->ch_layout, &track.CodecContext->ch_layout);
        pConvertedFrame->sample_rate = track.CodecContext->sample_rate;

        var ret = ffmpeg.av_frame_get_buffer(pConvertedFrame, 0);
        if (ret < 0)
          throw new Exception($"리샘플링 출력 프레임 버퍼 할당 실패: {GetFFmpegErrorString(ret)} (Code: {ret})");

        var convertedSamples = ffmpeg.swr_convert(
          track.Swr,
          (byte**)&pConvertedFrame->data,
          numSamples,
          &inputBuffer,
          numSamples
        );

        switch (convertedSamples)
        {
          case < 0:
            throw new Exception(
              $"오디오 리샘플링 실패: {GetFFmpegErrorString(convertedSamples)} (Code: {convertedSamples})"
            );
          case 0:
            return;
        }

        var written = ffmpeg.av_audio_fifo_write(track.Fifo, (void**)&pConvertedFrame->data, convertedSamples);
        if (written < convertedSamples)
          throw new Exception("오디오 FIFO 쓰기 실패");

        var frameSize = track.CodecContext->frame_size > 0 ? track.CodecContext->frame_size : 1024;
        while (ffmpeg.av_audio_fifo_size(track.Fifo) >= frameSize)
        {
          var audioWritable = ffmpeg.av_frame_make_writable(track.Frame);
          if (audioWritable < 0)
            throw new Exception(
              $"오디오 프레임 버퍼를 쓰기 가능 상태로 만들지 못했습니다: {GetFFmpegErrorString(audioWritable)}"
            );
          track.Frame->nb_samples = frameSize;

          var read = ffmpeg.av_audio_fifo_read(track.Fifo, (void**)&track.Frame->data, frameSize);
          if (read < frameSize)
            throw new Exception("오디오 FIFO 읽기 실패");

          track.Frame->pts = track.SampleIndex;
          track.SampleIndex += frameSize;

          SendAudioFrameAndWritePackets(track, track.Frame);
        }
      }
      finally
      {
        ffmpeg.av_frame_free(&pConvertedFrame);
      }
    }

    /// <summary>오디오 프레임을 해당 트랙 인코더에 전송하고 패킷을 파일에 기록합니다.</summary>
    private void SendAudioFrameAndWritePackets(AudioTrackState track, AVFrame* pFrame)
    {
      var response = ffmpeg.avcodec_send_frame(track.CodecContext, pFrame);
      if (response < 0)
      {
        throw new Exception(
          $"오디오 인코더에 프레임을 전송하는 도중 에러가 발생했습니다: {GetFFmpegErrorString(response)} (Code: {response})"
        );
      }

      try
      {
        while (true)
        {
          response = ffmpeg.avcodec_receive_packet(track.CodecContext, _pAudioPacket);
          if (response == ffmpeg.AVERROR(ffmpeg.EAGAIN) || response == ffmpeg.AVERROR_EOF)
            break;

          if (response < 0)
          {
            throw new Exception(
              $"오디오 인코더에서 패킷을 수신하는 도중 에러가 발생했습니다: {GetFFmpegErrorString(response)} (Code: {response})"
            );
          }

          ffmpeg.av_packet_rescale_ts(_pAudioPacket, track.CodecContext->time_base, track.Stream->time_base);
          _pAudioPacket->stream_index = track.Stream->index;

          response = ffmpeg.av_interleaved_write_frame(_pFormatContext, _pAudioPacket);
          if (response < 0)
          {
            throw new Exception(
              $"오디오 패킷을 파일에 쓰는 도중 에러가 발생했습니다: {GetFFmpegErrorString(response)} (Code: {response})"
            );
          }

          ffmpeg.av_packet_unref(_pAudioPacket);
        }
      }
      finally
      {
        ffmpeg.av_packet_unref(_pAudioPacket);
      }
    }

    /// <summary>모든 오디오 트랙의 잔여 샘플을 인코딩하고 인코더를 플러시합니다.</summary>
    private void FlushAudioEncoder()
    {
      foreach (var track in _audioTracks)
      {
        if (track.Fifo == null || track.CodecContext == null)
          continue;

        var remaining = ffmpeg.av_audio_fifo_size(track.Fifo);
        if (remaining > 0)
        {
          var audioWritable = ffmpeg.av_frame_make_writable(track.Frame);
          if (audioWritable < 0)
            throw new Exception(
              $"오디오 프레임 버퍼를 쓰기 가능 상태로 만들지 못했습니다: {GetFFmpegErrorString(audioWritable)}"
            );
          track.Frame->nb_samples = remaining;

          var read = ffmpeg.av_audio_fifo_read(track.Fifo, (void**)&track.Frame->data, remaining);
          if (read > 0)
          {
            track.Frame->pts = track.SampleIndex;
            track.SampleIndex += read;
            SendAudioFrameAndWritePackets(track, track.Frame);
          }
        }

        SendAudioFrameAndWritePackets(track, null);
      }
    }

    /// <summary>모든 오디오 트랙의 FFmpeg 리소스를 해제합니다. Dispose() 내부에서 호출됩니다.</summary>
    private void DisposeAudioResources()
    {
      foreach (var track in _audioTracks)
      {
        if (track.Swr != null)
        {
          var swr = track.Swr;
          ffmpeg.swr_free(&swr);
          track.Swr = null;
        }
        if (track.Fifo != null)
        {
          ffmpeg.av_audio_fifo_free(track.Fifo);
          track.Fifo = null;
        }
        if (track.Frame != null)
        {
          var pFrame = track.Frame;
          ffmpeg.av_frame_free(&pFrame);
          track.Frame = null;
        }
        if (track.CodecContext != null)
        {
          var pCtx = track.CodecContext;
          ffmpeg.avcodec_free_context(&pCtx);
          track.CodecContext = null;
        }
      }
      _audioTracks.Clear();

      if (_pAudioPacket != null)
      {
        var pPacket = _pAudioPacket;
        ffmpeg.av_packet_free(&pPacket);
        _pAudioPacket = null;
      }

      _audioEnabled = false;
    }
  }
}
