using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DevFlow.Sample;

/// <summary>
/// Shared todo data store used by both Native and Blazor UI tabs.
/// Registered as a singleton in DI.
/// </summary>
public class TodoService
{
    private int _nextItemId = 1;

    public ObservableCollection<TodoItem> Items { get; } = new();

    /// <summary>
    /// Raised when items or their completion states change.
    /// Blazor components subscribe to this for re-rendering.
    /// </summary>
    public event Action? Changed;

    public TodoService()
    {
        Items.CollectionChanged += (_, _) => NotifyChanged();
        ResetToIntegrationSeed();
    }

    internal void ResetToIntegrationSeed()
    {
        Items.Clear();
        _nextItemId = 1;
        Add("Buy groceries", id: "todo-buy-groceries");
        Add("Walk the dog", id: "todo-walk-dog");
        Add("Finish Microsoft.Maui.DevFlow project", id: "todo-finish-devflow");
    }

    public void Add(string title, string description = "", string? id = null)
    {
        Items.Add(new TodoItem
        {
            Id = string.IsNullOrWhiteSpace(id) ? $"todo-{_nextItemId++:D4}" : id,
            Title = title,
            Description = description,
        });
        NotifyChanged();
    }

    public void Remove(TodoItem item)
    {
        Items.Remove(item);
        NotifyChanged();
    }

    public void ToggleCompleted(TodoItem item)
    {
        item.IsCompleted = !item.IsCompleted;
        NotifyChanged();
    }

    public int TotalCount => Items.Count;
    public int CompletedCount => Items.Count(t => t.IsCompleted);
    public string Summary => $"{TotalCount} items, {CompletedCount} completed";

    public void NotifyChanged() => Changed?.Invoke();
}
