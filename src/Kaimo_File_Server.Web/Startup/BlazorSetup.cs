using Kaimo_File_Server.Web.Controllers.Api;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Startup;

/// <summary>Blazor Server, MVC controllers (client API JSON conventions) and named HTTP clients.</summary>
internal static class BlazorSetup
{
    public static void AddKaimoBlazorAndControllers(this WebApplicationBuilder builder)
    {
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            // InputFile reports the metadata of every selected file in ONE message, and a folder
            // upload also sends each file's folder. The 32 KB default fails at a few hundred files.
            // fileUpload.js (MAX_MESSAGE_BYTES) refuses folder selections that would not fit; keep
            // both values in step.
            .AddHubOptions(options => options.MaximumReceiveMessageSize = 1024 * 1024);

        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddAuthorizationCore();

        builder.Services.AddControllers()
            // Serialize/accept enums (e.g. SyncMode) as their names in the client API JSON.
            .AddJsonOptions(options =>
                options.JsonSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter()))
            // Malformed bodies/parameters on the /api/v1 client API get the uniform ApiError
            // envelope instead of ProblemDetails, so clients branch on one shape. Model-state
            // details are not echoed back. Other [ApiController]s keep the default response.
            .ConfigureApiBehaviorOptions(options =>
            {
                var defaultFactory = options.InvalidModelStateResponseFactory;
                options.InvalidModelStateResponseFactory = context =>
                    context.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor action
                    && typeof(ApiControllerBase).IsAssignableFrom(action.ControllerTypeInfo)
                        ? new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(
                            new ApiError("invalid_request", "The request body or parameters are malformed."))
                        : defaultFactory(context);
            });
        builder.Services.AddHttpClient(nameof(OneDriveDeviceAuthorizationService), client =>
            client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddHttpClient("CloudAccessOneDrive", client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient(nameof(DropboxAuthorizationService), client =>
            client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddHttpClient("CloudAccessDropbox", client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
    }
}
