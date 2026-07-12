using Microsoft.AspNetCore.Mvc.ActionConstraints;

namespace Apiext.Fs.Controllers;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireQueryParameterAttribute(params string[] names) : Attribute, IActionConstraint
{
    public int Order => 0;

    public bool Accept(ActionConstraintContext context)
    {
        var query = context.RouteContext.HttpContext.Request.Query;
        return names.Any(query.ContainsKey);
    }
}
