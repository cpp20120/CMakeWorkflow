using PolyType;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IHostNotifications
{
    [JsonRpcMethod("tooling/progress")]
    void Progress(DoctorProgress progress);

    [JsonRpcMethod("tooling/fixProgress")]
    void FixProgress(FixProgress progress);
}
