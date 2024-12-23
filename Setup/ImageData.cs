namespace Setup;

public readonly struct ImageData(byte[] data, uint width, uint height)
{
    public readonly byte[] Data = data;
    public readonly uint Width = width;
    public readonly uint Height = height;
}