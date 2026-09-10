using FortnitePorting.Services;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FortnitePorting.Swagger;

/// <summary>
/// Documents the <c>version</c>/<c>loadVersion</c> query parameters on the controllers that accept
/// them. They are handled by <see cref="VersionParameterFilter"/> rather than by the action
/// signatures, so Swagger would otherwise not show them at all.
/// </summary>
public sealed class VersionParameterOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var controller = context.MethodInfo.DeclaringType;
        if (controller == null || !controller.IsDefined(typeof(VersionAwareAttribute), inherit: true))
        {
            return;
        }

        var isJa = context.DocumentName == "ja";
        operation.Parameters ??= [];

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = VersionParameterFilter.VersionParameter,
            In = ParameterLocation.Query,
            Required = false,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            Example = "++Fortnite+Release-42.10-CL-57566230-Windows",
            Description = isJa
                ? "読み取るビルド。省略すると配信中のビルドです。「++Fortnite+Release-42.10-CL-57566230-Windows」のような完全な文字列のほか、42.10、CL番号、previous、latest も使えます。既定では読み込み済みのビルドのみが対象で、未読み込みなら 409 を返します。"
                : "Build to read from; the live build when omitted. Accepts the full string such as ++Fortnite+Release-42.10-CL-57566230-Windows, as well as 42.10, a changelist number, previous, or latest. Only already-loaded builds are served by default; naming an unloaded one returns 409."
        });

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = VersionParameterFilter.LoadParameter,
            In = ParameterLocation.Query,
            Required = false,
            Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean, Default = false },
            Description = isJa
                ? "version で指定したビルドが未読み込みのとき、その場でマウントするか。1ビルドのマウントには数分かかります。既定は false。"
                : "Mount the build named by version when it is not loaded yet. Mounting a build takes minutes. Default false."
        });
    }
}
