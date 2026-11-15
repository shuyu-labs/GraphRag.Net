using GraphRag.Net.Base;
using Microsoft.Extensions.DependencyInjection;

namespace GraphRag.Net.Repositories
{
    [ServiceDescription(typeof(IClaims_Repositories), ServiceLifetime.Scoped)]
    public class Claims_Repositories : Repository<Claims>, IClaims_Repositories
    {
    }
}
