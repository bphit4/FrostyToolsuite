namespace FrostyEditor.Managers;

public sealed record SoundOperationResult(bool Success, string Message)
{
	public static SoundOperationResult Unavailable(string message)
	{
		return new SoundOperationResult(Success: false, message);
	}
}
