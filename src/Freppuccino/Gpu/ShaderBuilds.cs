namespace Freppuccino.Gpu;

public static class ShaderBuilds
{
    public static bool Active { get; private set; }

    public static event Action? Changed;

    internal static void Set(bool active)
    {
        if (active == Active) return;
        Active = active;
        Changed?.Invoke();
    }
}
