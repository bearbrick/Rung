using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Rung.Host;

/// <summary>
/// 把数值类型的 schema 从 <c>["integer","string"]</c> 收窄成 <c>integer</c>。
/// <para>
/// .NET 的 OpenAPI 生成器之所以写成联合类型，是因为 System.Text.Json
/// <b>可以</b>被配置成接受字符串形式的数字。但 Rung 没这么配，实际序列化出来
/// 永远是数值。留着这个联合类型，等于逼每一个消费者去处理一种不会发生的情况——
/// 生成的 TypeScript 里 <c>number | string</c> 会污染所有算术运算。
/// </para>
/// <para>
/// schema 应当描述接口<b>实际产出</b>什么，而不是理论上能接受什么。
/// </para>
/// </summary>
internal sealed class NumericSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (schema.Type is not { } type)
        {
            return Task.CompletedTask;
        }

        var isNumeric = type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number);

        if (isNumeric && type.HasFlag(JsonSchemaType.String))
        {
            schema.Type = type & ~JsonSchemaType.String;

            // pattern 是为字符串形式准备的，收窄之后它只会误导人
            schema.Pattern = null;
        }

        return Task.CompletedTask;
    }
}
