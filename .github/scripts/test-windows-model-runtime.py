"""Exercise the shipped C ABI and ONNX Runtime; stdlib only, no downloads."""
import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import tempfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--backend", choices=["cpu", "directml"], default="cpu")
    parser.add_argument("--device-id", type=int)
    args = parser.parse_args()
    directory = args.directory.resolve(strict=True)
    fixtures = Path(__file__).resolve().parents[2] / "tests/fixtures/noise-model"
    runtime = directory / "onnxruntime.dll"
    # A missing runtime must fail, not turn this test into a skip or use a
    # machine-wide runtime. Load dependencies beside the exact shipped DLL.
    abi = c.CDLL(str(directory / "seiza_cabi.dll"), winmode=0x1100)
    pointer = c.c_void_p
    error_pointer = c.POINTER(pointer)
    signatures = {
        "seiza_string_free": ([pointer], None),
        "seiza_model_backends_json": ([c.c_char_p, error_pointer], pointer),
        "seiza_noise_model_open": ([c.c_char_p, c.c_char_p, error_pointer], pointer),
        "seiza_noise_model_free": ([pointer], None),
        "seiza_noise_model_denoise": ([pointer, c.POINTER(c.c_float), c.c_size_t, c.c_size_t, c.c_size_t, c.c_size_t, pointer, pointer, pointer, error_pointer], pointer),
        "seiza_denoised_residual": ([pointer, c.c_size_t, c.POINTER(c.c_size_t)], c.POINTER(c.c_float)),
        "seiza_denoised_report_json": ([pointer], c.c_char_p),
        "seiza_denoised_blend": ([pointer, c.c_float, c.c_float, c.c_float, c.POINTER(c.c_float), c.c_size_t, error_pointer], c.c_bool),
        "seiza_denoised_free": ([pointer], None),
        "seiza_noise_model_denoise_file": ([pointer, c.c_char_p, c.c_bool, pointer, pointer, pointer, error_pointer], pointer),
        "seiza_file_restoration_write": ([pointer, c.c_char_p, c.c_char_p, c.c_float, c.c_float, c.c_float, error_pointer], pointer),
        "seiza_file_restoration_free": ([pointer], None),
        "seiza_cancel_signal_create": ([], pointer),
        "seiza_cancel_signal_cancel": ([pointer], None),
        "seiza_cancel_signal_free": ([pointer], None),
        "seiza_onnxruntime_installed_json": ([c.c_char_p, error_pointer], pointer),
        "seiza_onnxruntime_install_json": ([c.c_char_p, pointer, pointer, pointer, error_pointer], pointer),
    }
    for name, (arguments, result) in signatures.items():
        function = getattr(abi, name)
        function.argtypes, function.restype = arguments, result

    def owned(value):
        if not value:
            return ""
        try:
            return c.string_at(value).decode("utf-8")
        finally:
            abi.seiza_string_free(value)

    error = pointer()

    def checked(result):
        message = owned(error.value)
        error.value = None
        if not result or message:
            raise AssertionError(message or "Native call returned no result")
        return result

    options = {"library": str(runtime), "backend": args.backend, "precision": "f32"}
    if args.device_id is not None:
        options["deviceId"] = args.device_id
    encoded = json.dumps(options).encode()
    backends = json.loads(owned(checked(abi.seiza_model_backends_json(encoded, c.byref(error)))))
    assert Path(backends["runtime"]["path"]).resolve() == runtime
    assert any(b["backend"] == "directml" and b["unavailable"] is None for b in backends["backends"]), backends
    print("Packaged ONNX Runtime:", backends["runtime"], flush=True)

    expected = json.loads((fixtures / "tiny-expected.json").read_text())
    raw = (fixtures / "image.f32").read_bytes()
    pixels = (c.c_float * (len(raw) // 4)).from_buffer_copy(raw)
    model = checked(abi.seiza_noise_model_open(str(fixtures / "tiny").encode(), encoded, c.byref(error)))
    try:
        result = checked(abi.seiza_noise_model_denoise(model, pixels, len(pixels), expected["width"], expected["height"], 1, None, None, None, c.byref(error)))
        try:
            report = json.loads(abi.seiza_denoised_report_json(result))
            assert report["backend"] == args.backend and report["precision"] == "f32"
            assert abs(report["channels"][0]["noise"] / expected["noise"] - 1) < 1e-5
            length = c.c_size_t()
            residual = abi.seiza_denoised_residual(result, 0, c.byref(length))
            assert residual and length.value == len(pixels)
            samples = iter(expected["residual"])
            for y in range(0, expected["height"], expected["stride"]):
                for x in range(0, expected["width"], expected["stride"]):
                    wanted = next(samples)
                    actual = residual[y * expected["width"] + x]
                    assert abs(actual - wanted) <= 1e-4 * max(expected["noise"], abs(wanted)), (x, y, actual, wanted)
            output = (c.c_float * len(pixels))()
            checked(abi.seiza_denoised_blend(result, 1, 1, 8, output, len(output), c.byref(error)))
            assert math.isnan(output[0]), "Missing coverage was lost"
            assert any(math.isfinite(a) and abs(a - b) > 0.01 for a, b in zip(output, pixels)), "Inference had no effect"
            print(f"{args.backend} f32 inference matches the recorded residuals; blending preserves NaNs.", flush=True)
        finally:
            abi.seiza_denoised_free(result)

        with tempfile.TemporaryDirectory(prefix="seiza-runtime-test-") as temp:
            folder = Path(temp)
            source = fixtures / "tiny-input.fits"
            original_hash = hashlib.sha256(source.read_bytes()).hexdigest()
            restored = checked(abi.seiza_noise_model_denoise_file(model, str(source).encode(), False, None, None, None, c.byref(error)))
            try:
                output_path = folder / "restored.fits"
                report = json.loads(owned(checked(abi.seiza_file_restoration_write(restored, str(output_path).encode(), None, 1, 1, 8, c.byref(error)))))
                assert report["backend"] == args.backend and output_path.stat().st_size > 2880
                assert b"SEIZAMDL" in output_path.read_bytes()[:2880 * 5], "Missing model provenance"
            finally:
                abi.seiza_file_restoration_free(restored)
            assert hashlib.sha256(source.read_bytes()).hexdigest() == original_hash
            signal = checked(abi.seiza_cancel_signal_create())
            try:
                abi.seiza_cancel_signal_cancel(signal)
                stopped = abi.seiza_noise_model_denoise(model, pixels, len(pixels), expected["width"], expected["height"], 1, None, signal, None, c.byref(error))
                assert not stopped and "cancel" in owned(error.value).lower()
                error.value = None
                install_path = str(folder / "runtime").encode()
                assert json.loads(owned(checked(abi.seiza_onnxruntime_installed_json(install_path, c.byref(error))))) is None
                installed = abi.seiza_onnxruntime_install_json(install_path, None, signal, None, c.byref(error))
                assert not installed and "cancel" in owned(error.value).lower()
                error.value = None
                assert not (folder / "runtime").exists()
            finally:
                abi.seiza_cancel_signal_free(signal)
        print("Full FITS export, source preservation, inference cancellation, and runtime-install cancellation passed.", flush=True)
    finally:
        abi.seiza_noise_model_free(model)


if __name__ == "__main__":
    main()
