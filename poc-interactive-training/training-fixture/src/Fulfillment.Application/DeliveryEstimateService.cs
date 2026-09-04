namespace TrainingFixture.Fulfillment;

public sealed class DeliveryEstimateService
{
    public DateOnly Resolve(DateOnly originalEstimate, DateOnly? confirmedDelayedEstimate) =>
        confirmedDelayedEstimate ?? originalEstimate;
}