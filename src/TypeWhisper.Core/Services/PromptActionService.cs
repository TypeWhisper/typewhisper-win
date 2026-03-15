using Microsoft.Data.Sqlite;
using TypeWhisper.Core.Data;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class PromptActionService : IPromptActionService
{
    private readonly ITypeWhisperDatabase _db;
    private List<PromptAction> _cache = [];
    private bool _cacheLoaded;

    public IReadOnlyList<PromptAction> Actions
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public IReadOnlyList<PromptAction> EnabledActions
    {
        get
        {
            EnsureCacheLoaded();
            return _cache
                .Where(a => a.IsEnabled)
                .OrderBy(a => a.SortOrder)
                .ToList();
        }
    }

    public event Action? ActionsChanged;

    public PromptActionService(ITypeWhisperDatabase db)
    {
        _db = db;
    }

    public void AddAction(PromptAction action)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO prompt_actions
            (id, name, system_prompt, icon, is_preset, is_enabled, sort_order,
             provider_override, model_override, target_action_plugin_id, hotkey_key,
             created_at, updated_at)
            VALUES (@id, @name, @prompt, @icon, @preset, @enabled, @sort,
                    @provider, @model, @target_action, @hotkey_key, @created, @updated)
            """;
        BindParams(cmd, action);
        cmd.ExecuteNonQuery();

        _cache.Add(action);
        ActionsChanged?.Invoke();
    }

    public void UpdateAction(PromptAction action)
    {
        var updated = action with { UpdatedAt = DateTime.UtcNow };

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE prompt_actions
            SET name = @name, system_prompt = @prompt, icon = @icon,
                is_preset = @preset, is_enabled = @enabled, sort_order = @sort,
                provider_override = @provider, model_override = @model,
                target_action_plugin_id = @target_action, hotkey_key = @hotkey_key,
                updated_at = @updated
            WHERE id = @id
            """;
        BindParams(cmd, updated);
        cmd.ExecuteNonQuery();

        var idx = _cache.FindIndex(a => a.Id == action.Id);
        if (idx >= 0) _cache[idx] = updated;
        ActionsChanged?.Invoke();
    }

    public void DeleteAction(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM prompt_actions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        _cache.RemoveAll(a => a.Id == id);
        ActionsChanged?.Invoke();
    }

    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        using var conn = _db.GetConnection();
        conn.Open();

        for (var i = 0; i < orderedIds.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE prompt_actions SET sort_order = @sort WHERE id = @id";
            cmd.Parameters.AddWithValue("@sort", i);
            cmd.Parameters.AddWithValue("@id", orderedIds[i]);
            cmd.ExecuteNonQuery();

            var idx = _cache.FindIndex(a => a.Id == orderedIds[i]);
            if (idx >= 0) _cache[idx] = _cache[idx] with { SortOrder = i };
        }

        ActionsChanged?.Invoke();
    }

    public void SeedPresets()
    {
        EnsureCacheLoaded();
        if (_cache.Any(a => a.IsPreset)) return;

        var presets = new (string Name, string Icon, string Prompt)[]
        {
            ("Translate to English", "\U0001F30D",
                "Translate the following text to English. Return only the translated text, no explanations."),
            ("Write Email", "\u2709\uFE0F",
                "Rewrite the following text as a professional email. Keep the same meaning and tone but make it polished and suitable for business communication. Return only the email body."),
            ("Format as List", "\U0001F4CB",
                "Convert the following text into a clean bullet-point list. Return only the formatted list."),
            ("Action Items", "\u2705",
                "Extract all action items and tasks from the following text. Return them as a numbered list. If no action items are found, say so briefly."),
            ("Reply", "\U0001F4AC",
                "Write a concise, professional reply to the following message. Match the tone of the original. Return only the reply text.")
        };

        for (var i = 0; i < presets.Length; i++)
        {
            var (name, icon, prompt) = presets[i];
            AddAction(new PromptAction
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                SystemPrompt = prompt,
                Icon = icon,
                IsPreset = true,
                SortOrder = i
            });
        }
    }

    private static void BindParams(SqliteCommand cmd, PromptAction a)
    {
        cmd.Parameters.AddWithValue("@id", a.Id);
        cmd.Parameters.AddWithValue("@name", a.Name);
        cmd.Parameters.AddWithValue("@prompt", a.SystemPrompt);
        cmd.Parameters.AddWithValue("@icon", a.Icon);
        cmd.Parameters.AddWithValue("@preset", a.IsPreset ? 1 : 0);
        cmd.Parameters.AddWithValue("@enabled", a.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@sort", a.SortOrder);
        cmd.Parameters.AddWithValue("@provider", (object?)a.ProviderOverride ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@model", (object?)a.ModelOverride ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@target_action", (object?)a.TargetActionPluginId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hotkey_key", (object?)a.HotkeyKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", a.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updated", a.UpdatedAt.ToString("o"));
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, system_prompt, icon, is_preset, is_enabled, sort_order,
                   provider_override, model_override, created_at, updated_at,
                   target_action_plugin_id, hotkey_key
            FROM prompt_actions ORDER BY sort_order, name
            """;

        _cache = [];
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _cache.Add(new PromptAction
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                SystemPrompt = reader.GetString(2),
                Icon = reader.GetString(3),
                IsPreset = reader.GetInt32(4) != 0,
                IsEnabled = reader.GetInt32(5) != 0,
                SortOrder = reader.GetInt32(6),
                ProviderOverride = reader.IsDBNull(7) ? null : reader.GetString(7),
                ModelOverride = reader.IsDBNull(8) ? null : reader.GetString(8),
                CreatedAt = DateTime.Parse(reader.GetString(9)),
                UpdatedAt = DateTime.Parse(reader.GetString(10)),
                TargetActionPluginId = reader.IsDBNull(11) ? null : reader.GetString(11),
                HotkeyKey = reader.IsDBNull(12) ? null : reader.GetString(12)
            });
        }
        _cacheLoaded = true;
    }
}
