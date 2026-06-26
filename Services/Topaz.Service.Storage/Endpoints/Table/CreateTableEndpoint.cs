using Topaz.EventPipeline;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Topaz.Service.Shared;
using Topaz.Service.Storage.Models.Requests;
using Topaz.Shared;

namespace Topaz.Service.Storage.Endpoints.Table;

internal sealed class CreateTableEndpoint(Pipeline eventPipeline, ITopazLogger logger)
    : TableDataPlaneEndpointBase(eventPipeline, logger), IEndpointDefinition
{
    public string? ProviderNamespace => "Microsoft.Storage";

    // The legacy Microsoft.Azure.Storage / Cosmos.Table SDK posts table creates to
    // "/Tables()" (trailing empty OData parens); modern clients use "/Tables".
    // Router treats a leading '^' segment as a regex, so accept both forms.
    public string[] Endpoints => ["POST /^Tables(\\(\\))?$"];

    public string[] Permissions => ["Microsoft.Storage/storageAccounts/tableServices/tables/write"];

    public void GetResponse(HttpContext context, HttpResponseMessage response, GlobalOptions options)
    {
        if (RejectIfSecondaryHostForMutation(context.Request.Headers, response)) return;
        if (!TryGetStorageAccount(context.Request.Headers, out var storageAccount))
        {
            response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        var subscriptionIdentifier = storageAccount!.GetSubscription();
        var resourceGroupIdentifier = storageAccount!.GetResourceGroup();

        if (!IsRequestAuthorized(subscriptionIdentifier, resourceGroupIdentifier, storageAccount.Name, context, response))
            return;

        Logger.LogDebug(nameof(CreateTableEndpoint), nameof(GetResponse), "Executing {0}.",
            nameof(CreateTableEndpoint));

        using var sr = new StreamReader(context.Request.Body);
        var rawContent = sr.ReadToEnd();
        var request = JsonSerializer.Deserialize<CreateTableRequest>(rawContent, GlobalSettings.JsonOptions);

        if (request == null || string.IsNullOrEmpty(request.TableName))
        {
            response.StatusCode = HttpStatusCode.BadRequest;
            return;
        }

        var tableExists = ControlPlane.CheckIfTableExists(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccount.Name, request.TableName);
        if (tableExists)
        {
            WriteTableAlreadyExists(response);
            return;
        }

        try
        {
            var tableOp = ControlPlane.CreateTable(subscriptionIdentifier, resourceGroupIdentifier,
                storageAccount.Name, request);

            if (!context.Request.Headers.TryGetValue("Prefer", out var prefer) || prefer != "return-no-content")
            {
                response.Content = JsonContent.Create(tableOp.Resource);
                response.StatusCode = HttpStatusCode.Created;
                response.Headers.Add("Preference-Applied", "return-content");
            }

            if (prefer == "return-no-content")
            {
                response.StatusCode = HttpStatusCode.NoContent;
                response.Headers.Add("Preference-Applied", "return-no-content");
            }
        }
        catch (InvalidOperationException)
        {
            // A concurrent CreateTable for the same table won the race between the existence check above and
            // this create (the file-backed store throws when the metadata file already exists). Azure treats a
            // duplicate create as 409 TableAlreadyExists, which the storage SDK's CreateIfNotExists tolerates,
            // so surface the same conflict instead of a 500 that would force the caller to retry.
            WriteTableAlreadyExists(response);
        }
    }

    private static void WriteTableAlreadyExists(HttpResponseMessage response)
    {
        var error = new TableErrorResponse("TableAlreadyExists", "Table already exists.");

        response.StatusCode = HttpStatusCode.Conflict;
        response.Headers.Add("x-ms-error-code", "TableAlreadyExists");
        response.Content = JsonContent.Create(error);
    }
}
