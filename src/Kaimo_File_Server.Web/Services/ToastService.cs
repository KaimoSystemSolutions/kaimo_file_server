namespace Kaimo_File_Server.Web.Services;

public enum ToastType { Info, Success, Error }

public record ToastMessage(string Id, string Text, ToastType Type, DateTime CreatedAt);

public class ToastService
{
    private readonly List<ToastMessage> _toasts = new();
    public IReadOnlyList<ToastMessage> Toasts => _toasts;
    public event Action? OnChanged;

    public void Show(string text, ToastType type = ToastType.Info)
    {
        _toasts.Add(new ToastMessage(Guid.NewGuid().ToString(), text, type, DateTime.Now));
        OnChanged?.Invoke();
    }

    public void Remove(string id)
    {
        _toasts.RemoveAll(t => t.Id == id);
        OnChanged?.Invoke();
    }
}