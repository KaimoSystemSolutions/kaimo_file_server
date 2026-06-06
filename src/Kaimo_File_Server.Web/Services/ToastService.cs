namespace Kaimo_File_Server.Web.Services;
public enum ToastType { Info, Success, Error, Progress }

public record ToastMessage(string Id, string Text, ToastType Type, DateTime CreatedAt)
{
    public int Progress { get; set; }
    public DateTime CreatedAt { get; set; } = CreatedAt;
}

public class ToastService
{
    private readonly List<ToastMessage> _toasts = new();
    public IReadOnlyList<ToastMessage> Toasts => _toasts;
    public event Action? OnChanged;

    public string Show(string text, ToastType type = ToastType.Info)
    {
        var id = Guid.NewGuid().ToString();
        _toasts.Add(new ToastMessage(id, text, type, DateTime.Now));
        OnChanged?.Invoke();
        return id;
    }

    public void Update(string id, string? text = null, int? progress = null, ToastType? type = null)
    {
        var toast = _toasts.FirstOrDefault(t => t.Id == id);
        if (toast is null) return;

        if (text is not null || type is not null)
        {
            var index = _toasts.IndexOf(toast);
            _toasts[index] = toast with
            {
                Text = text ?? toast.Text,
                Type = type ?? toast.Type,
                CreatedAt = DateTime.Now
            };
            toast = _toasts[index];
        }

        if (progress is not null)
            toast.Progress = progress.Value;

        OnChanged?.Invoke();
    }

    public void Remove(string id)
    {
        _toasts.RemoveAll(t => t.Id == id);
        OnChanged?.Invoke();
    }
}