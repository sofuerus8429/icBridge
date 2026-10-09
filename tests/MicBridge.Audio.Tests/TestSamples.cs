namespace MicBridge.Audio.Tests;

internal static class TestSamples
{
    internal static void Fill(float[] buffer, float value, int offset = 0, int? count = null)
    {
        int end = offset + (count ?? buffer.Length);
        for (int i = offset; i < end; i++) buffer[i] = value;
    }
}
