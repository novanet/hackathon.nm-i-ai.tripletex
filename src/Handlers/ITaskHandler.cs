using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public interface ITaskHandler
{
    Task HandleAsync(TripletexApiClient api, ExtractionResult extracted);
}
