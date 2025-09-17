using System.Threading.Tasks;
using KPIMonitor.Models;
namespace KPIMonitor.Services.Abstractions
{
    public interface IMemoPdfService
    {
        /// <summary>Generate the memo PDF as bytes.</summary>
        Task<byte[]> GenerateAsync(MemoDocument doc);
    }
}
