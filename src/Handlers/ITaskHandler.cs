using TripletexAgent.Models;
using TripletexAgent.Services;

namespace TripletexAgent.Handlers;

public interface ITaskHandler
{
    Task<HandlerResult> HandleAsync(TripletexApiClient api, ExtractionResult extracted);
}
