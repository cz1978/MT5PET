using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public static class PriceDistancePolicy
{
    public const int BehaviorBucketPoints = 50;

    public static decimal ToPriceDistance(
        decimal points,
        SymbolSpecification? specification,
        decimal fallbackDistance)
    {
        if (points <= 0m)
        {
            return 0m;
        }

        var step = specification?.PriceStep ?? 0m;
        return step > 0m ? points * step : Math.Max(0m, fallbackDistance);
    }

    public static decimal Bucket(
        decimal price,
        SymbolSpecification? specification,
        int bucketPoints = BehaviorBucketPoints)
    {
        var step = specification?.PriceStep ?? 0m;
        if (step <= 0m)
        {
            step = InferDecimalStep(price);
        }

        var width = step * Math.Max(1, bucketPoints);
        return decimal.Floor(price / width);
    }

    private static decimal InferDecimalStep(decimal value)
    {
        var bits = decimal.GetBits(value);
        var scale = (bits[3] >> 16) & 0x7F;
        var step = 1m;
        for (var index = 0; index < scale; index++)
        {
            step /= 10m;
        }
        return step;
    }
}
