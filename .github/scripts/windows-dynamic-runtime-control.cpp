// A deliberately /MD DLL, used only to prove the loader test blocks VC redist.
extern "C" __declspec(dllexport) int control()
{
    try { throw 42; }
    catch (int value) { return value; }
}
