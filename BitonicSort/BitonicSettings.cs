namespace BitonicSort;

sealed class BitonicSettings
{
    // The number of elements to be sorted. Must equal gridWidth * gridHeight || Workgroup Size * Workgroups * 2.
    // When changed, all relevant values within the settings object are reset to their defaults at the beginning of a sort with n elements.
    public required uint TotalElements;
    // The width of the screen in cells.
    public required uint GridWidth;
    // The height of the screen in cells.
    public required uint GridHeight;
    // Grid Dimensions as a tuple.
    public (uint Width, uint Height) GridDimensions => (GridWidth, GridHeight);

    // INVOCATION, WORKGROUP SIZE, AND WORKGROUP DISPATCH SETTINGS
    // The size of a workgroup, or the number of invocations executed within each workgroup
    // Determined algorithmically based on 'Size Limit', maxInvocationsX, and the current number of elements to sort
    public required uint WorkgroupSize;
    // An artificial constraint on the maximum workgroup size/maximum invocations per workgroup as specified by device.limits.maxComputeWorkgroupSizeX
    public required uint SizeLimit;
    // Total workgroups that are dispatched during each step of the bitonic sort
    public required uint WorkgroupsPerStep;

    // HOVER SETTINGS
    // The element/cell in the element visualizer directly beneath the mouse cursor  
    public int HoveredCell = 0;
    // The element/cell in the element visualizer that the hovered cell will swap with in the next execution step of the bitonic sort.
    public int SwappedCell = 1;

    // STEP INDEX, STEP TYPE, AND STEP SWAP SPAN SETTINGS
    // The index of the current step in the bitonic sort.
    public uint StepIndex = 0;

    // The total number of steps required to sort the displayed elements.
    public uint TotalSteps;
    // A tuple that condenses 'Step Index' and 'Total Steps' into a single element.
    public (uint Current, uint Total) CurrentStep => (StepIndex, TotalSteps);

    // The category of the previously executed step. Always begins the bitonic sort with a value of 'NONE' and ends with a value of 'DISPERSE_LOCAL'
    public StepType PrevStep = StepType.None;
    // The category of the next step that will be executed. Always begins the bitonic sort with a value of 'FLIP_LOCAL' and ends with a value of 'NONE'
    public StepType NextStep = StepType.FlipLocal;
    // The maximum span of a swap operation in the sort's previous step.
    public uint PrevSwapSpan = 0;
    // The maximum span of a swap operation in the sort's upcoming step.
    public uint NextSwapSpan = 2;

    // ANIMATION LOOP AND FUNCTION SETTINGS
    // A flag that designates whether we will dispatch a workload this frame.
    public bool ExecuteStep = false;

    // The speed at which each step of the bitonic sort will be executed after 'Auto Sort' has been called.
    public int AutoSortSpeedMs = 50;

    // MISCELLANEOUS SETTINGS
    public DisplayMode DisplayMode = DisplayMode.Elements;
    // An atomic value representing the total number of swap operations executed over the course of the bitonic sort.
    public uint TotalSwaps = 0;

    // TIMESTAMP SETTINGS
    // NOTE: Timestep values below all are calculated in terms of milliseconds rather than the nanoseconds a timestamp query set usually outputs.
    // Time taken to execute the previous step of the bitonic sort in milliseconds
    public double StepTimeMs = 0;
    // Total taken to colletively execute each step of the complete bitonic sort, represented in milliseconds.
    public double SortTimeMs = 0;
    // Average time taken to complete a bitonic sort with the current combination of n 'Total Elements' and x 'Size Limit'
    public double AverageSortTimeMs = 0;
    public readonly Dictionary<(uint TotalElements, uint SizeLimit), (uint Sorts, double TotalTimeMs)> ConfigToCompleteSwapsMap = new();
    public (uint TotalElements, uint SizeLimit) ConfigKey => (TotalElements, SizeLimit);
}