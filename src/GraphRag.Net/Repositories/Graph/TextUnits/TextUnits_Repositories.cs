using GraphRag.Net.Base;
using Microsoft.Extensions.DependencyInjection;

namespace GraphRag.Net.Repositories
{
    [ServiceDescription(typeof(ITextUnits_Repositories), ServiceLifetime.Scoped)]
    public class TextUnits_Repositories : Repository<TextUnits>, ITextUnits_Repositories
    {
    }
}
