namespace Seiza.App.Services;

internal sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        int leftIndex = 0;
        int rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
            {
                int result = CompareNumber(left, ref leftIndex, right, ref rightIndex);
                if (result != 0)
                {
                    return result;
                }

                continue;
            }

            int characterResult = char.ToUpperInvariant(left[leftIndex])
                .CompareTo(char.ToUpperInvariant(right[rightIndex]));
            if (characterResult != 0)
            {
                return characterResult;
            }

            leftIndex++;
            rightIndex++;
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    private static int CompareNumber(
        string left,
        ref int leftIndex,
        string right,
        ref int rightIndex)
    {
        int leftStart = leftIndex;
        int rightStart = rightIndex;

        while (leftIndex < left.Length && left[leftIndex] == '0')
        {
            leftIndex++;
        }

        while (rightIndex < right.Length && right[rightIndex] == '0')
        {
            rightIndex++;
        }

        int leftDigitsStart = leftIndex;
        int rightDigitsStart = rightIndex;
        while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
        {
            leftIndex++;
        }

        while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
        {
            rightIndex++;
        }

        int leftDigits = leftIndex - leftDigitsStart;
        int rightDigits = rightIndex - rightDigitsStart;
        if (leftDigits != rightDigits)
        {
            return leftDigits.CompareTo(rightDigits);
        }

        for (int offset = 0; offset < leftDigits; offset++)
        {
            int digitResult = left[leftDigitsStart + offset]
                .CompareTo(right[rightDigitsStart + offset]);
            if (digitResult != 0)
            {
                return digitResult;
            }
        }

        int leftRunLength = leftIndex - leftStart;
        int rightRunLength = rightIndex - rightStart;
        return leftRunLength.CompareTo(rightRunLength);
    }
}
