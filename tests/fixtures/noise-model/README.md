These small synthetic fixtures come from `theatrus/seiza` commit
60d72ca2c62826dcbbbce78367f329f4c815330d, `seiza-model/tests/fixtures`.
They are covered by Seiza's Apache-2.0 license. The tiny graph is a test
network, not the published astronomical noise model.

`test-windows-model-runtime.py` uses the packaged C ABI and ONNX DLL to compare
inference with the trainer's recorded residual samples. No network or Python
packages are needed. Production model testing on real data supplements this
check; the fixture does not measure restoration quality.
