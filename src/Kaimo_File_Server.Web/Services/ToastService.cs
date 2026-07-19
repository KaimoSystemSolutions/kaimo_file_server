namespace Kaimo_File_Server.Web.Services;
public enum ToastType { Info, Success, State, Error, Progress }
public record ToastMessage(string Id, string Text, ToastType Type, DateTime CreatedAt)
{
    public int Progress { get; set; }
    public DateTime CreatedAt { get; set; } = CreatedAt;
    public Func<Task>? OnDismiss { get; set; }
}

public class ToastService
{
    private readonly List<ToastMessage> _toasts = new();
    public IReadOnlyList<ToastMessage> Toasts => _toasts;
    public event Action? OnChanged;

    public string Show(string text, ToastType type = ToastType.Info, Func<Task>? onDismiss = null)
    {
        var id = Guid.NewGuid().ToString();
        _toasts.Add(new ToastMessage(id, text, type, DateTime.Now) { OnDismiss = onDismiss });
        OnChanged?.Invoke();
        return id;
    }

    public void Update(string id, string? text = null, int? progress = null, 
        ToastType? type = null, Func<Task>? onDismiss = null)
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

        if (onDismiss is not null)
            toast.OnDismiss = onDismiss;

        OnChanged?.Invoke();
    }

    public void Remove(string? id)
    {
        if(id == null)
            return;
        
        _toasts.RemoveAll(t => t.Id == id);
        OnChanged?.Invoke();
    }
}