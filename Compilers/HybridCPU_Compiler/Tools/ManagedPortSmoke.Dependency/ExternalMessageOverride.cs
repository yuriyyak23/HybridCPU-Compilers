namespace ManagedPortSmoke.Dependency;

public class ExternalMessageOverride : System.Exception
{
    public override string Message => null!;
}
