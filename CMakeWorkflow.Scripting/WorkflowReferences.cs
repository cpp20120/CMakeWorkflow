using System.Text.Json;

namespace CMakeWorkflow.Scripting;

/// <summary>An immutable named context owned by one authoring scope.</summary>
public sealed class ContextRef
{
    internal ContextRef(Workflow owner, string id, JsonElement definition)
    {
        (Owner, Id, Definition) = (owner, id, definition);
    }

    internal Workflow Owner { get; }
    internal JsonElement Definition { get; }
    public string Id { get; }
}

/// <summary>An immutable declaration. Sharing this reference shares its prerequisites.</summary>
public class NodeRef
{
    internal NodeRef(Workflow owner, string id, ContextRef? context, NodeRef[] prerequisites, JsonElement definition)
    {
        (Owner, Id, Context, Prerequisites, Definition) = (owner, id, context, prerequisites, definition);
    }

    internal Workflow Owner { get; }
    internal ContextRef? Context { get; }
    internal NodeRef[] Prerequisites { get; }
    internal JsonElement Definition { get; }
    public string Id { get; }
}

/// <summary>A native build declaration that can supply an executable to run or profile.</summary>
public sealed class TargetBuildRef : NodeRef
{
    internal TargetBuildRef(Workflow owner, string id, ContextRef context, NodeRef[] prerequisites, JsonElement definition)
        : base(owner, id, context, prerequisites, definition)
    {
    }
}
