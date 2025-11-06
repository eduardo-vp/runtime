using System;
using System.Threading.Tasks;

public class Async2Void
{
    public static void Main()
    {
        var t = AsyncTestEntryPoint(123, 456);
        t.Wait();
        Console.WriteLine(t.Result);
    }

    private static async Task<int> AsyncTestEntryPoint(int x, int y)
    {
        int result = await OtherAsync(x, y);
        return result;
    }

    private static async Task<int> OtherAsync(int x, int y)
    {
        Console.WriteLine(x);
        Console.WriteLine(y);
        return x + y;
    }
}
