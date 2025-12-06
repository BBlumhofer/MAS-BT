namespace MAS_BT.Nodes.SkillControl;

/// <summary>
/// Interface für asynchrone Callable-Operationen (Pause, Resume, Abort, etc.)
/// </summary>
public interface IAsyncCallable
{
    Task CallAsync(string methodName, Dictionary<string, string>? parameters = null);
}
