namespace PriceDemo;

public static class Checkout
{
    public static float CalculateTotal(int quantity, bool member)
    {
        int units = Math.Max(quantity, 0);
        float unitPrice = 2.5f;
        float subtotal = units * unitPrice;
        if (member)
            subtotal *= 0.9f;

        return subtotal;
    }
}
