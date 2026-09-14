using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

public static class EndpointRegistry
{
    public static void MapAll(IEndpointRouteBuilder app, WebState state)
    {
        CoreEndpoints.Map(app, state);
        // T2.8: CatalogEndpoints.Map(app, state); ExportEndpoints.Map(app, state);
        // T3.5: MappingEndpoints.Map(app, state);
        // T4.5: SqlEndpoints.Map(app, state);
        // T5.5: TransferEndpoints.Map(app, state);
    }
}
