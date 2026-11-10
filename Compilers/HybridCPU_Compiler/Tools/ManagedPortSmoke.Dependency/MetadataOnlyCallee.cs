namespace ManagedPortSmoke.Dependency;

public static class FallbackOwner<T>
{
    public static T Identity(T value) => value;
    public static U MethodIdentity<U>(U value) => value;
    public static T Next(T previous, T next) => next;
    public static U MethodNext<U>(U previous, U next) => next;
    public static T Choose(int index, T previous, T next) => index == 0 ? previous : next;
    public static U MethodChoose<U>(int index, U previous, U next) => index == 0 ? previous : next;
}

public static class MetadataOnlyCallee
{
    public static int AdjustFallback(int value) => unchecked(value + 1);
    public static long AdvancePayload(long value) => unchecked(value + 1L);
    public static long AdvancePayloadBy(long value, long delta) => unchecked(value + delta);
    public static int CombineFallback(int value, int delta) => unchecked(value + delta);
    public static T FallbackIdentity<T>(T value) => value;
    public static T FallbackNext<T>(T previous, T next) => next;
    public static T FallbackChoose<T>(int index, T previous, T next) => index == 0 ? previous : next;
    public static long AdvanceMixed(long value, int delta) => unchecked(value + delta);
    public static long AdvanceCounted(long payload) { for(int index=0;index<2;index++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceBounded(long payload,int limit) { for(int index=0;index<limit;index++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceStride(long payload) { for(int index=0;index<4;index+=2)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceStrideRounded(long payload) { for(int index=0;index<5;index+=3)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceSeed(long payload) { for(int index=1;index<5;index+=2)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceNegativeSeed(long payload) { for(int index=-3;index<2;index+=2)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceDescending(long payload) { for(int index=5;index>1;index-=2)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceDescendingRounded(long payload) { for(int index=-1;index>-6;index-=2)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceInclusive(long payload) { for(int index=5;index>=1;index-=2)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceInclusiveAscending(long payload) { for(int index=1;index<=5;index+=2)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceUnsigned(long payload) { for(uint index=0;index<3;index++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceUnsignedHigh(long payload) { for(uint index=2147483648U;index<2147483651U;index++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceUnsigned64(long payload) { for(ulong index=0;index<3;index++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceUnsigned64High(long payload) { for(ulong index=9223372036854775808UL;index<9223372036854775811UL;index++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceSigned64(long payload) { for(long index=0;index<3;index++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceSigned64Negative(long payload) { for(long index=long.MinValue;index<long.MinValue+3;index++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceSigned64Bound(long payload,long limit) { for(long index=0;index<limit;index++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceSequential(long payload) { for(long index=0;index<2;index++)payload=unchecked(payload+1L);for(long next=0;next<2;next++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceSequentialOrdered(long payload) { for(long index=0;index<2;index++)payload=unchecked(payload+1L);payload=unchecked(payload*3L);for(long next=0;next<2;next++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceNested(long payload) { for(long index=0;index<2;index++)for(long next=0;next<2;next++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceDependent(long payload) { for(long index=0;index<3;index++)for(long next=0;next<index;next++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceAffine(long payload) { for(long index=0;index<3;index++)for(long next=0;next<index+1;next++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceScaled(long payload) { for(long index=0;index<3;index++)for(long next=0;next<2*index+1;next++)payload=unchecked(payload+1L);return payload; }
    public static long AdvanceScaledOrdered(long payload) { for(long index=0;index<3;index++)for(long next=0;next<2*index+1;next++)payload=unchecked(payload*3L+1L);return payload; }
    public static long AdvanceAffineOrdered(long payload) { for(long index=0;index<3;index++)for(long next=0;next<index+1;next++)payload=unchecked(payload*3L+1L);return payload; }
    public static long AdvanceDependentOrdered(long payload) { for(long index=0;index<3;index++)for(long next=0;next<index;next++)payload=unchecked(payload*3L+1L);return payload; }
    public static long AdvanceNestedOrdered(long payload) { for(long index=0;index<2;index++)for(long next=0;next<2;next++)payload=unchecked(payload*3L+1L);return payload; }
    public static int Read() => 42;
    public static T Identity<T>(T value) => value;
    public static int CallIdentity(int value) => Identity<int>(value);

    public static int CatchDivide(int value)
    {
        try { return 42 / value; }
        catch (System.DivideByZeroException) { return -1; }
    }
}
