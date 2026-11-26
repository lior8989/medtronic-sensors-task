using Sensor.Communication.Protocol;
using Sensor.Control.Status;
using Sensor.Control.Supervision;

namespace Sensor.WebApi.Contracts;

/// <summary>Live system status: returned by <c>GET /api/status</c> and pushed by <c>GET /api/events</c>.</summary>
/// <param name="Version">Increases when the system snapshot changes (results are tracked separately).</param>
/// <param name="SystemState">Initializing, Configuring, Running or Faulted.</param>
/// <param name="FaultReason">Why the system is Faulted, e.g. "Camera reported Error"; null otherwise.</param>
/// <param name="Camera">Camera state, connection and latest raw image.</param>
/// <param name="Pointer">Pointer state, connection and latest raw position.</param>
/// <param name="LatestResult">Latest calculated color with the image and position used; null if none yet.</param>
/// <param name="Processing">Pairing and processing counters.</param>
public sealed record StatusResponse(
    long Version,
    SystemState SystemState,
    string? FaultReason,
    CameraStatusDto Camera,
    PointerStatusDto Pointer,
    ResultDto? LatestResult,
    ProcessingStatsDto Processing);

/// <summary>Camera sensor status.</summary>
/// <param name="State">Latest reported state (Setup, Ready, Error), or null if not reported yet.</param>
/// <param name="Connection">NeverSeen, Connected (valid traffic within 1 s) or Disconnected.</param>
/// <param name="SessionId">Current session ID as 16 hex digits, or null if never seen.</param>
/// <param name="LastSeenMsAgo">Milliseconds since the latest valid traffic, or null if never seen.</param>
/// <param name="LatestImage">Latest raw image of the current session (any system state), or null.</param>
public sealed record CameraStatusDto(
    SensorState? State,
    ConnectionStatus Connection,
    string? SessionId,
    double? LastSeenMsAgo,
    ImageDto? LatestImage);

/// <summary>Pointer sensor status.</summary>
/// <param name="State">Latest reported state (Setup, Ready, Error), or null if not reported yet.</param>
/// <param name="Connection">NeverSeen, Connected (valid traffic within 1 s) or Disconnected.</param>
/// <param name="SessionId">Current session ID as 16 hex digits, or null if never seen.</param>
/// <param name="LastSeenMsAgo">Milliseconds since the latest valid traffic, or null if never seen.</param>
/// <param name="LatestPosition">Latest raw position of the current session (any system state), or null.</param>
public sealed record PointerStatusDto(
    SensorState? State,
    ConnectionStatus Connection,
    string? SessionId,
    double? LastSeenMsAgo,
    PositionSampleDto? LatestPosition);

/// <summary>A Camera image.</summary>
/// <param name="Sequence">Camera output sequence of the measurement.</param>
/// <param name="AgeMs">Milliseconds since it was received.</param>
/// <param name="Width">Image width (10).</param>
/// <param name="Height">Image height (10).</param>
/// <param name="RgbaBase64">400 bytes, row-major, top row first, R,G,B,A per pixel, Base64-encoded.</param>
public sealed record ImageDto(ulong Sequence, double AgeMs, int Width, int Height, string RgbaBase64);

/// <summary>A Pointer position sample.</summary>
/// <param name="Sequence">Pointer output sequence of the measurement.</param>
/// <param name="AgeMs">Milliseconds since it was received.</param>
/// <param name="X">Column, 0..9 (0 = left).</param>
/// <param name="Y">Row, 0..9 (0 = top).</param>
public sealed record PositionSampleDto(ulong Sequence, double AgeMs, int X, int Y);

/// <summary>A pixel position.</summary>
/// <param name="X">Column, 0..9.</param>
/// <param name="Y">Row, 0..9.</param>
public sealed record PositionDto(int X, int Y);

/// <summary>An RGBA color (A is 255).</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha.</param>
public sealed record ColorDto(int R, int G, int B, int A);

/// <summary>A calculated result and the inputs used.</summary>
/// <param name="Color">The calculated pixel color.</param>
/// <param name="Position">The Pointer position used.</param>
/// <param name="Image">The Camera image used.</param>
/// <param name="PointerSequence">Pointer output sequence of the position used.</param>
/// <param name="ImageAgeAtPairingMs">How much older the image was than the position when paired (0..50 ms).</param>
/// <param name="AgeMs">Milliseconds since the result was calculated.</param>
public sealed record ResultDto(
    ColorDto Color,
    PositionDto Position,
    ImageDto Image,
    ulong PointerSequence,
    double ImageAgeAtPairingMs,
    double AgeMs);

/// <summary>Pairing and processing counters since startup.</summary>
/// <param name="PairsSubmitted">Pairs sent for processing.</param>
/// <param name="PairsProcessed">Results published.</param>
/// <param name="PairsReplaced">Pairs replaced by a newer one while waiting (latest wins).</param>
/// <param name="SkippedNoImage">Pointer samples with no image yet in the run.</param>
/// <param name="SkippedStaleImage">Pointer samples whose latest image was older than the max age.</param>
public sealed record ProcessingStatsDto(
    long PairsSubmitted,
    long PairsProcessed,
    long PairsReplaced,
    long SkippedNoImage,
    long SkippedStaleImage);
