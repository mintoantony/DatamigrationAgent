using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

public static class EndpointRegistry
{
    public static void MapAll(IEndpointRouteBuilder app, WebState state)
    {
        CoreEndpoints.Map(app, state);
        CatalogEndpoints.Map(app, state);   // T2.8
        ExportEndpoints.Map(app, state);    // T2.8
        MappingEndpoints.Map(app, state);   // T3.5
        SqlExports.Map(app, state);         // T4.4
        SqlEndpoints.Map(app, state);       // T4.5
        TransferEndpoints.Map(app, state);  // T5.5
    }
}
