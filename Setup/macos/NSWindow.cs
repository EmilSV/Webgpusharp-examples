namespace Setup.Macos;


internal class NSWindow
{
    public readonly nint NativePtr;

    public NSWindow(nint ptr)
    {
        NativePtr = ptr;
    }

    public NSView contentView => ObjectiveCRuntime.objc_msgSend<NSView>(NativePtr, "contentView");
}