namespace Seiza.App.Services;

public sealed class SeizaCoreException : Exception
{
    public SeizaCoreException(string message)
        : base(message)
    {
    }
}
