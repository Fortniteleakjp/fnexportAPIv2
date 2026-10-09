using System.IO.Compression;
using FortnitePorting.Swagger;
using Microsoft.AspNetCore.ResponseCompression;

namespace FortnitePorting.Services;

public static class ApiEndpoints
{
    public static IServiceCollection AddApiEndpoints(this IServiceCollection services)
    {
        services.AddControllers(options => options.Filters.Add<VersionParameterFilter>());
        services.AddScoped<RequestBuildProvider>();
        services.AddMemoryCache();
        services.AddSingleton(ProviderReloadGate.Instance);
        services.AddEndpointsApiExplorer();
        services.AddResponseCompression(options =>
        {
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ["application/json", "text/plain", "text/csv", "application/xml", "text/xml"];
        });
        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("ja", new Microsoft.OpenApi.OpenApiInfo
            {
                Title = "Fortnite アセットエクスポート API", Version = "v1",
                Description = "ローカルで実行するCUE4ParseベースのFortniteアセット解析APIです。アセットのJSON・画像・音声取得、コスメ検索、ファイル検索、PAK/INI確認、依存関係解析、ローカライズを提供します。VPSなどへのホスティングを前提としません。"
            });
            options.SwaggerDoc("en", new Microsoft.OpenApi.OpenApiInfo
            {
                Title = "Fortnite Asset Analysis API", Version = "v1",
                Description = "A local CUE4Parse-powered Fortnite asset analysis API. Provides JSON/image/audio export, cosmetic search, path/content search, PAK and INI inspection, dependency analysis, and localization. It is designed to run locally rather than as a VPS-hosted service."
            });
            var xml = Path.Combine(AppContext.BaseDirectory, "FortnitePorting.xml");
            if (File.Exists(xml)) options.IncludeXmlComments(xml, includeControllerXmlComments: true);
            options.OperationFilter<LocalizedOperationFilter>();
            options.OperationFilter<VersionParameterOperationFilter>();
            options.DocumentFilter<LocalizedDocumentFilter>();
        });
        services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
            .WithExposedHeaders("X-Audio-Format", "X-Audio-Decoded", "X-Rada-Native-Decoder", "Content-Disposition",
                "X-Usmap-Bytes", "X-Usmap-Enums", "X-Usmap-Structs", "X-Usmap-Output", "X-Usmap-Loaded", "X-Usmap-Source", "X-Usmap-Build",
                "X-Backup-Entries", "X-Backup-Version", "X-Hotfix-Status", "X-Hotfix-Applied", "X-Build-Version", "X-Build-Is-Live",
                "X-Changes-Mode", "X-Changes-Modified", "X-Changes-Unverified", "X-Changes-Computed", "X-Changes-Forced", "X-Changes-Excluded-Archives",
                "X-Icon-Source", "X-Icon-Name")));
        return services;
    }

    public static WebApplication UseApiEndpoints(this WebApplication app)
    {
        app.UseResponseCompression();
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/ja/swagger.json", "日本語 (Japanese)");
            options.SwaggerEndpoint("/swagger/en/swagger.json", "English");
            options.RoutePrefix = "swagger";
            options.DocumentTitle = "Fortnite Asset Analysis API";
            options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
            options.DefaultModelsExpandDepth(1);
            options.DisplayRequestDuration();
            options.EnableFilter();
        });
        app.UseRouting();
        app.UseCors();
        app.UseMiddleware<ProviderReloadMiddleware>();
        app.MapControllers();
        app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
        return app;
    }
}
