using System.Text.Json.Nodes;
using Json.Schema;
using Shared.Services;

namespace Cli.Utils;

public static class FlowMapValidator
{
    private static readonly JsonSchema Schema = LoadSchema();

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(FlowMapValidator).Assembly
            .GetManifestResourceStream("Cli.Schemas.workflow-map.schema.json")
            ?? throw new InvalidOperationException("embedded workflow map schema not found");

        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    public static string? ValidateSchema(JsonNode map)
    {
        return Schema.Evaluate(map).IsValid ? null : "map does not match course-1 schema";
    }

    public static async Task<string?> ValidateSemanticsAsync(
        JsonNode map, ActionCatalogService catalog)
    {
        var steps = map["steps"] as JsonArray ?? [];
        var transitions = map["transitions"] as JsonArray ?? [];
        var startStep = map["start_step"]?.GetValue<string>();

        var stepKeys = new HashSet<string>(StringComparer.Ordinal);
        var stepTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var automaticTasks = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var waitSignals = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var manualOutcomes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var step in steps.OfType<JsonObject>())
        {
            var key = step["key"]?.GetValue<string>();
            var type = step["type"]?.GetValue<string>();
            if (key is null || type is null)
                return "step is missing key or type";
            if (!stepKeys.Add(key))
                return $"duplicate step key {key}";
            stepTypes[key] = type;
            switch (type)
            {
                case "automatic":
                    automaticTasks[key] = step["task"] as JsonObject ?? new JsonObject();
                    break;
                case "wait_signal":
                    waitSignals[key] = step;
                    break;
                case "manual":
                    manualOutcomes[key] = step;
                    break;
            }
        }

        if (startStep is null || !stepKeys.Contains(startStep))
            return $"start_step {startStep} does not exist";

        var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seenTransitions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transition in transitions.OfType<JsonObject>())
        {
            var from = transition["from"]?.GetValue<string>();
            var outcome = transition["outcome"]?.GetValue<string>();
            var to = transition["to"]?.GetValue<string>();
            if (from is null || outcome is null || to is null)
                return "transition is missing from, outcome or to";
            if (!stepKeys.Contains(from) || !stepKeys.Contains(to))
                return $"transition {from}->{to} references an unknown step";
            if (!seenTransitions.Add($"{from}\0{outcome}"))
                return $"duplicate transition for outcome {outcome} from step {from}";
            if (stepTypes.TryGetValue(from, out var fromType) && fromType == "end")
                return $"transition from end step {from} is not allowed";
            if (!outgoing.TryGetValue(from, out var list))
                outgoing[from] = list = [];
            list.Add(outcome);
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(startStep);
        reachable.Add(startStep);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outgoing.TryGetValue(current, out var targets))
                continue;
            foreach (var target in targets)
            {
                var next = transitions.OfType<JsonObject>()
                    .First(t => t["from"]?.GetValue<string>() == current && t["outcome"]?.GetValue<string>() == target)
                    ["to"]!.GetValue<string>();
                if (reachable.Add(next))
                    queue.Enqueue(next);
            }
        }

        foreach (var key in stepKeys)
        {
            if (!reachable.Contains(key))
                return $"step {key} is not reachable from start_step";
        }

        if (!stepKeys.Any(key => stepTypes[key] == "end" && reachable.Contains(key)))
            return "no reachable end step";

        if (HasCycle(startStep, outgoing, transitions))
            return "flow contains a cycle";

        foreach (var key in stepKeys)
        {
            var type = stepTypes[key];
            var expected = new HashSet<string>(StringComparer.Ordinal);
            var actual = outgoing.TryGetValue(key, out var list)
                ? new HashSet<string>(list, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            switch (type)
            {
                case "automatic":
                {
                    var task = automaticTasks[key];
                    if (task["service"]?.GetValue<string>() != "postgres")
                        return $"task of step {key} must use service postgres";
                    var module = task["module"]?.GetValue<string>();
                    var action = task["action"]?.GetValue<string>();
                    var actionVersion = task["action_version"]?.GetValue<int>();
                    if (module is null || action is null || actionVersion is null)
                        return $"task of step {key} is missing module, action or action_version";

                    var manifest = await catalog.GetOrDefault(module, action, actionVersion);
                    if (manifest is null)
                        return $"action {module}.{action} version {actionVersion} is not published and enabled";
                    if (!manifest.Enabled)
                        return $"action {module}.{action} version {actionVersion} is disabled";

                    foreach (var outcome in manifest.Outcomes)
                        expected.Add(outcome);
                    if (!actual.SetEquals(expected))
                        return $"step {key} must declare exactly one transition per action outcome";

                    var taskPolicy = task["required_policy"] as JsonArray ?? [];
                    var actionPolicy = manifest.RequiredPolicy;
                    if (!new HashSet<string>(taskPolicy.Select(p => p?.GetValue<string>() ?? ""), StringComparer.Ordinal)
                            .SetEquals(new HashSet<string>(actionPolicy, StringComparer.Ordinal)))
                        return $"task required_policy of step {key} must equal action required_policy";

                    var retryError = ValidateRetry(task["retry"]);
                    if (retryError is not null)
                        return retryError;

                    var mappingError = ValidateMapping(task);
                    if (mappingError is not null)
                        return mappingError;
                    break;
                }
                case "wait_signal":
                {
                    var outcome = waitSignals[key]["outcome"]?.GetValue<string>();
                    if (outcome is null)
                        return $"wait_signal step {key} is missing outcome";
                    expected.Add(outcome);
                    if (!actual.SetEquals(expected))
                        return $"step {key} must declare exactly one transition for outcome {outcome}";
                    break;
                }
                case "manual":
                {
                    var outcomes = manualOutcomes[key]["allowed_outcomes"] as JsonArray ?? [];
                    if (outcomes.Count == 0)
                        return $"manual step {key} must declare allowed_outcomes";
                    foreach (var outcome in outcomes)
                        expected.Add(outcome?.GetValue<string>() ?? "");
                    if (!actual.SetEquals(expected))
                        return $"step {key} must declare exactly one transition per allowed outcome";
                    break;
                }
                case "end":
                    if (actual.Count > 0)
                        return $"end step {key} must not declare transitions";
                    break;
            }
        }

        return null;
    }

    private static string? ValidateRetry(JsonNode? retry)
    {
        if (retry is not JsonObject retryObject)
            return "task retry policy is required";
        var maxAttempts = retryObject["max_attempts"]?.GetValue<int>() ?? 0;
        var delays = retryObject["delays_ms"] as JsonArray ?? [];
        if (maxAttempts < 1 || maxAttempts > 10)
            return "max_attempts must be in range 1..10";
        if (delays.Count != maxAttempts - 1)
            return "delays_ms must contain exactly max_attempts - 1 values";
        return null;
    }

    private static string? ValidateMapping(JsonObject task)
    {
        var mapping = task["input_mapping"] as JsonObject ?? new JsonObject();
        var constants = task["input_constants"] as JsonObject ?? new JsonObject();

        var targets = new List<string>();
        foreach (var property in mapping)
        {
            var target = property.Key;
            var source = property.Value?.GetValue<string>();
            if (!target.StartsWith('/') || source is null
                || !source.StartsWith('/'))
                return "input_mapping must use RFC 6901 JSON pointers";
            targets.Add(target);
        }

        foreach (var constant in constants)
            targets.Add("/" + constant.Key);

        for (var i = 0; i < targets.Count; i++)
        {
            for (var j = i + 1; j < targets.Count; j++)
            {
                if (PointersOverlap(targets[i], targets[j]))
                    return $"mapping targets {targets[i]} and {targets[j]} overlap";
            }
        }

        return null;
    }

    private static bool PointersOverlap(string left, string right)
    {
        var l = left.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        var r = right.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        var length = Math.Min(l.Length, r.Length);
        for (var i = 0; i < length; i++)
        {
            if (!string.Equals(l[i], r[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool HasCycle(
        string start, Dictionary<string, List<string>> outgoing, JsonArray transitions)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);

        return Dfs(start);

        bool Dfs(string current)
        {
            if (onStack.Contains(current))
                return true;
            if (!visited.Add(current))
                return false;
            onStack.Add(current);

            if (outgoing.TryGetValue(current, out var outcomes))
            {
                foreach (var outcome in outcomes)
                {
                    var to = transitions.OfType<JsonObject>()
                        .First(t => t["from"]?.GetValue<string>() == current && t["outcome"]?.GetValue<string>() == outcome)
                        ["to"]!.GetValue<string>();
                    if (Dfs(to))
                        return true;
                }
            }

            onStack.Remove(current);
            return false;
        }
    }
}