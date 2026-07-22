#include "begin_gate.h"
#include "capture_session.h"
#include "event_writer.h"
#include "options.h"
#include "path_policy.h"
#include "probe.h"
#include "string_utils.h"

#include <windows.h>

#include <cstdio>
#include <format>
#include <iostream>
#include <string>

namespace wgc {

namespace {

constexpr int kExitSuccess = 0;
constexpr int kExitFailure = 1;

void PrintHelp() {
    std::cout <<
        "wgc-native-helper.exe\n"
        "  --capture-continuous-display\n"
        "  --display-bounds <x,y,width,height>\n"
        "  --recording-id <safe-id>\n"
        "  --output <absolute-mp4-path>\n"
        "  --duration-ms <1000..10000>\n"
        "  --fps <1..60>\n"
        "  --begin-signal <absolute-path>\n"
        "  --begin-token <unguessable-token>\n"
        "  --begin-timeout-ms <bounded-ms>\n"
        "  --stop-signal <absolute-path>\n"
        "  --i-understand-this-captures-screen\n"
        "\n"
        "Additional modes:\n"
        "  --help\n"
        "  --version\n"
        "  --probe\n";
}

void PrintVersion() {
    std::cout << "wgc-native-helper 0.1.0\n";
}

void PrintProbeResult(const ProbeResult& result) {
    std::cout << "RESULT: " << (result.ok ? "OK" : "FAIL") << "\n";
    std::cout << "WgcSupported: " << (result.wgcSupported ? "true" : "false") << "\n";
    std::cout << "D3d11Initialized: " << (result.d3d11Initialized ? "true" : "false") << "\n";
    std::cout << "EncoderCreated: " << (result.encoderCreated ? "true" : "false") << "\n";
    if (!result.error.empty()) {
        std::cout << "Reason: " << result.error << "\n";
    }
}

std::string HresultToString(HRESULT hr) {
    return std::format("0x{:08X}", static_cast<unsigned>(hr));
}

bool ValidateContinuousOptions(const Options& opts, std::string& error) {
    if (opts.mode != CaptureMode::ContinuousDisplay) {
        error = "Expected --capture-continuous-display";
        return false;
    }
    if (!opts.hasConsentFlag) {
        error = "Missing --i-understand-this-captures-screen";
        return false;
    }
    if (!IsValidRecordingId(opts.recordingId)) {
        error = "Invalid recording-id; expected 1..64 alphanumeric, '-', '_', '.'";
        return false;
    }
    if (opts.outputPath.empty()) {
        error = "Missing --output";
        return false;
    }
    if (opts.displayBounds.width <= 0 || opts.displayBounds.height <= 0) {
        error = "Invalid display-bounds; width and height must be positive";
        return false;
    }
    if (opts.durationMs < 1000 || opts.durationMs > 10000) {
        error = "Invalid duration-ms; expected 1000..10000";
        return false;
    }
    if (opts.fps < 1 || opts.fps > 60) {
        error = "Invalid fps; expected 1..60";
        return false;
    }
    if (opts.beginSignalPath.empty()) {
        error = "Missing --begin-signal";
        return false;
    }
    if (opts.beginToken.empty()) {
        error = "Missing --begin-token";
        return false;
    }
    if (opts.beginTimeoutMs < 100 || opts.beginTimeoutMs > 300000) {
        error = "Invalid begin-timeout-ms; expected 100..300000";
        return false;
    }
    if (opts.stopSignalPath.empty()) {
        error = "Missing --stop-signal";
        return false;
    }
    return true;
}

bool PathsEqualCaseInsensitive(const std::wstring& a, const std::wstring& b) {
    return _wcsicmp(a.c_str(), b.c_str()) == 0;
}

CaptureOutcome MakeValidationFail(const std::string& errorCode,
                                  const std::string& reason) {
    CaptureOutcome outcome;
    outcome.result = CaptureResult::Failed;
    outcome.errorCode = errorCode;
    outcome.reason = reason;
    return outcome;
}

int ExitCodeFromOutcome(const CaptureOutcome& outcome) {
    switch (outcome.result) {
        case CaptureResult::Success:
        case CaptureResult::Stopped:
            return kExitSuccess;
        case CaptureResult::Failed:
            return kExitFailure;
    }
    return kExitFailure;
}

int RunContinuous(const Options& opts) {
    EventWriter writer;

    PathPolicy policy = PathPolicy::CreateDefault();
    PathCheckResult outputCheck = ValidateOutputPath(opts.outputPath, policy);
    if (!outputCheck.ok) {
        WriteTerminalOutcome(writer, MakeValidationFail("invalid_output", outputCheck.error));
        return kExitFailure;
    }

    PathCheckResult beginCheck = ValidateControlPath(opts.beginSignalPath, policy);
    if (!beginCheck.ok) {
        WriteTerminalOutcome(writer, MakeValidationFail("invalid_begin_signal", beginCheck.error));
        return kExitFailure;
    }

    PathCheckResult stopCheck = ValidateControlPath(opts.stopSignalPath, policy);
    if (!stopCheck.ok) {
        WriteTerminalOutcome(writer, MakeValidationFail("invalid_stop_signal", stopCheck.error));
        return kExitFailure;
    }

    if (PathsEqualCaseInsensitive(beginCheck.canonicalPath, stopCheck.canonicalPath)) {
        WriteTerminalOutcome(writer, MakeValidationFail("invalid_arguments",
            "begin-signal and stop-signal must be different paths"));
        return kExitFailure;
    }

    // Atomically reserve the partial file so a concurrent helper cannot publish
    // to the same path. The placeholder is closed immediately; the encoder will
    // open it by path, but CREATE_NEW already prevented a collision.
    HANDLE partialHandle = CreatePartialPlaceholder(outputCheck.partialPath);
    if (partialHandle == INVALID_HANDLE_VALUE) {
        WriteTerminalOutcome(writer, MakeValidationFail("partial_placeholder_failed",
            "Unable to reserve partial output file"));
        return kExitFailure;
    }
    ::CloseHandle(partialHandle);

    // Use canonical control paths throughout the runtime; do not pass the raw
    // CLI strings into the capture pipeline.
    Options runtimeOpts = opts;
    runtimeOpts.beginSignalPath = beginCheck.canonicalPath;
    runtimeOpts.stopSignalPath = stopCheck.canonicalPath;

    BeginGate gate(runtimeOpts.beginSignalPath, runtimeOpts.beginToken,
                   runtimeOpts.stopSignalPath, runtimeOpts.beginTimeoutMs);
    CaptureSession session(runtimeOpts, outputCheck.canonicalPath, outputCheck.partialPath, gate, writer);

    CaptureOutcome outcome;
    try {
        outcome = session.Run();
    } catch (const std::exception& ex) {
        outcome = NormalizeFailureEvidence(
            MakeValidationFail("internal_error", ex.what()),
            outputCheck.partialPath);
        WriteTerminalOutcome(writer, outcome);
        return ExitCodeFromOutcome(outcome);
    } catch (...) {
        outcome = NormalizeFailureEvidence(
            MakeValidationFail("internal_error", "Unexpected exception during capture"),
            outputCheck.partialPath);
        WriteTerminalOutcome(writer, outcome);
        return ExitCodeFromOutcome(outcome);
    }

    // RunContinuous is the single terminal owner for this capture. Normalize
    // failure evidence using the canonical partial path, then emit exactly one
    // terminal event.
    outcome = NormalizeFailureEvidence(outcome, outputCheck.partialPath);
    WriteTerminalOutcome(writer, outcome);
    return ExitCodeFromOutcome(outcome);
}

} // namespace

} // namespace wgc

int wmain(int argc, wchar_t* argv[]) {
    using namespace wgc;

    try {
        // Set stdout to unbuffered so IPC events are flushed immediately.
        std::setvbuf(stdout, nullptr, _IONBF, 0);

        ParseResult parseResult = ParseArguments(argc, argv);
        if (!parseResult.error.empty()) {
            EventWriter writer;
            writer.Fail("invalid_arguments", parseResult.error, "", {}, 0, 0);
            return kExitFailure;
        }

        const Options& opts = parseResult.options;

        switch (opts.mode) {
            case CaptureMode::Help:
                PrintHelp();
                return kExitSuccess;
            case CaptureMode::Version:
                PrintVersion();
                return kExitSuccess;
            case CaptureMode::Probe: {
                ProbeResult result = RunProbe();
                PrintProbeResult(result);
                return result.ok ? kExitSuccess : kExitFailure;
            }
            case CaptureMode::ContinuousDisplay: {
                std::string error;
                if (!ValidateContinuousOptions(opts, error)) {
                    EventWriter writer;
                    writer.Fail("invalid_arguments", error, "", {}, 0, 0);
                    return kExitFailure;
                }
                return RunContinuous(opts);
            }
            default:
                break;
        }

        EventWriter writer;
        writer.Fail("invalid_arguments", "No capture mode specified", "", {}, 0, 0);
        return kExitFailure;
    } catch (const std::exception& ex) {
        EventWriter writer;
        writer.Fail("internal_error", ex.what(), "", {}, 0, 0);
        return kExitFailure;
    } catch (...) {
        EventWriter writer;
        writer.Fail("internal_error", "Unexpected fatal exception", "", {}, 0, 0);
        return kExitFailure;
    }
}
