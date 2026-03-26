using System.Threading.Tasks;

namespace AudioTransfer.Core.Models
{
    public interface IConfigRepository<T> where T : class, new()
    {
        Task<T> LoadOrDefaultAsync();
        Task SaveAsync(T config);
    }
}
