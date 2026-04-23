using Microsoft.SemanticKernel;

namespace HiveOps.Infrastructure.AI;

/// <summary>
/// IFunctionInvocationFilter that automatically injects TenantId into every
/// SK function invocation, ensuring no plugin can access data without the
/// correct tenant scope.
/// </summary>
public sealed class TenantAwareKernelFilter : IFunctionInvocationFilter
{
    private readonly Guid _tenantId;

    public TenantAwareKernelFilter(Guid tenantId)
    {
        _tenantId = tenantId;
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        // Inject TenantId before the function runs so every plugin can read it
        context.Arguments[KernelConstants.TenantIdKey] = _tenantId;
        context.Kernel.Data[KernelConstants.TenantIdKey] = _tenantId;

        await next(context);
    }
}
