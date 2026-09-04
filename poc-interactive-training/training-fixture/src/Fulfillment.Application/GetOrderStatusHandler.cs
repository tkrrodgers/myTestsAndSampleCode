namespace TrainingFixture.Fulfillment;

public sealed class GetOrderStatusHandler(DeliveryEstimateService estimates)
{
    public DateOnly Handle(DateOnly originalEstimate, DateOnly? confirmedDelayedEstimate) =>
        estimates.Resolve(originalEstimate, confirmedDelayedEstimate);
}