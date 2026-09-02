using System.Text.Json.Nodes;

namespace Workflow;

public static class JsonPointer
{
    public static JsonNode? Resolve(JsonNode root, string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
            return root;

        var current = root;
        foreach (var segment in Split(pointer))
        {
            if (current is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(Unescape(segment), out var next))
                    return null;
                current = next!;
            }
            else if (current is JsonArray array)
            {
                if (!int.TryParse(segment, out var index) || index < 0 || index >= array.Count)
                    return null;
                current = array[index]!;
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    public static bool Set(JsonNode root, string pointer, JsonNode? value)
    {
        if (pointer == "/")
            return false;

        var segments = Split(pointer);
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = Unescape(segments[i]);
            if (current is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(segment, out var next) || next is null)
                    return false;
                current = next;
            }
            else if (current is JsonArray array)
            {
                if (!int.TryParse(segment, out var index) || index < 0 || index >= array.Count)
                    return false;
                current = array[index]!;
            }
            else
            {
                return false;
            }
        }

        var last = Unescape(segments[^1]);
        if (current is JsonObject targetObject)
        {
            targetObject[last] = value?.DeepClone();
            return true;
        }
        if (current is JsonArray targetArray && int.TryParse(last, out var targetIndex)
            && targetIndex >= 0 && targetIndex < targetArray.Count)
        {
            targetArray[targetIndex] = value?.DeepClone();
            return true;
        }
        return false;
    }

    private static string[] Split(string pointer) =>
        pointer.Split(['/'], StringSplitOptions.RemoveEmptyEntries);

    private static string Unescape(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
}