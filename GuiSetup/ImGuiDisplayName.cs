namespace Setup;


[System.AttributeUsage(System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class ImGuiDisplayNameAttribute : System.Attribute
{
    // See the attribute guidelines at
    //  http://go.microsoft.com/fwlink/?LinkId=85236

    // This is a positional argument
    public ImGuiDisplayNameAttribute(string positionalString)
    {
        DisplayString = positionalString;
    }
    public string DisplayString { get; }
}