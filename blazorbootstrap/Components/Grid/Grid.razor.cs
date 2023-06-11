﻿namespace BlazorBootstrap;

public partial class Grid<TItem> : BaseComponent
{
    #region Members

    private RenderFragment? headerSelectionTemplate;

    /// <summary>
    /// Current grid state (filters, paging, sorting).
    /// </summary>
    internal GridState<TItem> gridCurrentState = new GridState<TItem>(1, null);

    private List<GridColumn<TItem>> columns = new List<GridColumn<TItem>>();

    private List<TItem> items = null;

    private HashSet<TItem> selectedItems = new HashSet<TItem>();

    private int pageSize;

    private int? totalCount = null;

    private int totalPages => GetTotalPagesCount();

    private string paginationItemsText => GetPaginationItemsText();

    private bool requestInProgress = false;

    private string responsiveCssClass => this.Responsive ? "table-responsive" : "";

    private object? lastAssignedDataOrDataProvider;

    private CancellationTokenSource cancellationTokenSource = default!;

    private CheckboxState headerCheckboxState = CheckboxState.Unchecked;

    private string headerCheckboxId = default!;

    private Dictionary<int, string> checkboxIds = new Dictionary<int, string>();

    public int SelectedItemsCount = 0;

    public bool allItemsSelected = false;

    private bool isFirstRenderComplete = false;

    #endregion Members

    #region Methods

    protected override void OnInitialized()
    {
        headerCheckboxId = IdGenerator.Generate;

        this.pageSize = this.PageSize;

        base.OnInitialized();
    }

    protected override Task OnParametersSetAsync()
    {
        if ((Data is null && DataProvider is null) || (Data is not null && DataProvider is not null))
        {
            throw new ArgumentException($"Grid requires either {nameof(Data)} or {nameof(DataProvider)}, but not both or neither.");
        }

        if (AllowPaging && PageSize < 0)
            throw new ArgumentException($"{nameof(PageSize)} must be greater than zero.");

        if (isFirstRenderComplete)
        {
            // Perform a re-query only if the data source or something else has changed
            var newDataOrDataProvider = Data; //?? (object?)DataProvider;
            var dataSourceHasChanged = newDataOrDataProvider != lastAssignedDataOrDataProvider;
            if (dataSourceHasChanged)
            {
                lastAssignedDataOrDataProvider = newDataOrDataProvider;
            }

            var mustRefreshData = dataSourceHasChanged && !GridSettingsChanged.HasDelegate;

            // page size changed
            if (!mustRefreshData && pageSize != PageSize)
            {
                mustRefreshData = true;
                pageSize = PageSize;
                _ = ResetPageNumberAsync(false);
                SaveGridSettingsAsync();
            }

            // We want to trigger the first data load when we've collected the initial set of columns
            // because they might perform some action, like setting the default sort order. 
            // It would be wasteful to have to re-query immediately.
            return (columns.Count > 0 && mustRefreshData) ? RefreshDataAsync(false, default) : Task.CompletedTask;
        }

        return base.OnParametersSetAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await RefreshDataAsync(firstRender);
            isFirstRenderComplete = true;
        }
        await base.OnAfterRenderAsync(firstRender);
    }

    internal void AddColumn(GridColumn<TItem> column)
    {
        columns.Add(column);
    }

    private async Task OnHeaderCheckboxChanged(ChangeEventArgs args)
    {
        allItemsSelected = bool.TryParse(args?.Value?.ToString(), out bool checkboxState) && checkboxState;
        selectedItems = allItemsSelected ? new(items) : new();
        SelectedItemsCount = selectedItems.Count;
        await CheckOrUnCheckAll();

        if (SelectedItemsChanged.HasDelegate)
            await SelectedItemsChanged.InvokeAsync(selectedItems);
    }

    private async Task OnRowCheckboxChanged(string id, TItem item, ChangeEventArgs args)
    {
        bool.TryParse(args?.Value?.ToString(), out bool isChecked);

        if (SelectionMode == GridSelectionMode.Multiple)
        {
            _ = isChecked ? selectedItems.Add(item) : selectedItems.Remove(item);
            SelectedItemsCount = selectedItems.Count;
            allItemsSelected = SelectedItemsCount == items.Count;

            if (allItemsSelected)
                await SetCheckboxStateAsync(headerCheckboxId, CheckboxState.Checked);
            else if (SelectedItemsCount == 0)
                await SetCheckboxStateAsync(headerCheckboxId, CheckboxState.Unchecked);
            else
                await SetCheckboxStateAsync(headerCheckboxId, CheckboxState.Indeterminate);
        }
        else
        {
            selectedItems = isChecked ? new() { item } : new();
            SelectedItemsCount = selectedItems.Count;
            allItemsSelected = false;
            await CheckOrUnCheckAll();
            await SetCheckboxStateAsync(id, isChecked ? CheckboxState.Checked : CheckboxState.Unchecked);
        }

        if (SelectedItemsChanged.HasDelegate)
            await SelectedItemsChanged.InvokeAsync(selectedItems);
    }

    private async Task CheckOrUnCheckAll()
    {
        await JS.InvokeVoidAsync("window.blazorBootstrap.grid.checkOrUnCheckAll", $".bb-grid-form-check-{headerCheckboxId} > input.form-check-input", allItemsSelected);
    }

    private async Task SetCheckboxStateAsync(string id, CheckboxState checkboxState)
    {
        await JS.InvokeVoidAsync("window.blazorBootstrap.grid.setSelectAllCheckboxState", id, (int)checkboxState);
    }

    /// <summary>
    /// Get filters.
    /// </summary>
    /// <returns>IEnumerable</returns>
    public IEnumerable<FilterItem> GetFilters()
    {
        if (!AllowFiltering || columns == null || !columns.Any())
            return null;

        return columns
                .Where(column => column.Filterable && column.GetFilterOperator() != FilterOperator.None && !string.IsNullOrWhiteSpace(column.GetFilterValue()))
                ?.Select(column => new FilterItem(column.PropertyName, column.GetFilterValue(), column.GetFilterOperator(), column.StringComparison));
    }

    /// <summary>
    /// Set filters.
    /// </summary>
    /// <param name="filterItems"></param>
    private void SetFilters(IEnumerable<FilterItem> filterItems)
    {
        if (filterItems is null || !filterItems.Any())
            return;

        foreach (var item in filterItems)
        {
            var column = columns.Where(x => x.PropertyName == item.PropertyName).FirstOrDefault();
            if (column != null)
            {
                var allowedFilterOperators = FilterOperatorHelper.GetFilterOperators(column.GetPropertyTypeName());
                if (allowedFilterOperators != null && allowedFilterOperators.Any(x => x.FilterOperator == item.Operator))
                {
                    column.SetFilterOperator(item.Operator);
                    column.SetFilterValue(item.Value);
                }
            }
        }
    }

    internal async Task FilterChangedAsync()
    {
        if (cancellationTokenSource is not null
            && !cancellationTokenSource.IsCancellationRequested)
        {
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
        }

        cancellationTokenSource = new CancellationTokenSource();

        var token = cancellationTokenSource.Token;
        await Task.Delay(300, token); // 300ms timeout for the debouncing

        await SaveGridSettingsAsync();
        await RefreshDataAsync(false, token);
    }

    private async Task OnPageChangedAsync(int newPageNumber)
    {
        gridCurrentState = new GridState<TItem>(newPageNumber, gridCurrentState.Sorting);
        await SaveGridSettingsAsync();
        await RefreshDataAsync(false, default);
    }

    private async Task OnPageSizeChangedAsync(ChangeEventArgs args)
    {
        int.TryParse(args?.Value?.ToString(), out int newPageSize);
        pageSize = PageSize = newPageSize;
        await ResetPageNumberAsync(false);
        await SaveGridSettingsAsync();
        await RefreshDataAsync(false, default);
    }

    /// <summary>
    /// Reset the page number to 1 and refresh the grid.
    /// </summary>
    public async ValueTask ResetPageNumber()
    {
        await ResetPageNumberAsync(true);
    }

    internal async ValueTask ResetPageNumberAsync(bool refreshGrid = false)
    {
        gridCurrentState = new GridState<TItem>(1, gridCurrentState.Sorting);

        if (refreshGrid)
            await RefreshDataAsync(false, default);
    }

    internal async Task SortingChangedAsync(GridColumn<TItem> column)
    {
        if (columns == null || !columns.Any())
            return;

        // check sorting enabled on any of the columns
        var sortedColumn = columns.FirstOrDefault(c => c.currentSortDirection != SortDirection.None);

        // reset other columns sorting
        columns.ForEach(c =>
        {
            if (c.ElementId != column.ElementId)
                c.currentSortDirection = SortDirection.None;

            // set default sorting
            if (sortedColumn == null && c.IsDefaultSortColumn)
            {
                if (c.ElementId == column.ElementId
                && c.currentSortDirection == SortDirection.None
                && c.defaultSortDirection == SortDirection.Descending)
                    c.currentSortDirection = SortDirection.Ascending; // Default Sorting: DESC                
                else
                    c.currentSortDirection = (c.defaultSortDirection != SortDirection.None) ? c.defaultSortDirection : SortDirection.Ascending;

                gridCurrentState = new GridState<TItem>(gridCurrentState.PageIndex, c.GetSorting());
            }
            else if (c.ElementId == column.ElementId && c.SortDirection != SortDirection.None)
            {
                gridCurrentState = new GridState<TItem>(gridCurrentState.PageIndex, c.GetSorting());
            }
        });

        await SaveGridSettingsAsync();
        await RefreshDataAsync(false, default);
    }

    private IEnumerable<SortingItem<TItem>> GetDefaultSorting()
    {
        if (!AllowSorting || columns == null || !columns.Any())
            return null;

        return columns?
                .Where(column => column.CanSort() && column.IsDefaultSortColumn)?
                .SelectMany(item => item.GetSorting());
    }

    private int GetTotalPagesCount()
    {
        if (totalCount.HasValue && totalCount.Value > 0)
        {
            var q = totalCount.Value / pageSize;
            var r = totalCount.Value % pageSize;

            if (q < 1)
                return 1;

            return q + (r > 0 ? 1 : 0);
        }

        return 1;
    }

    private async Task LoadGridSettingsAsync()
    {
        if (this.SettingsProvider is null)
            return;

        var settings = await this.SettingsProvider.Invoke();
        if (settings is null)
            return;

        if (settings.Filters is not null && settings.Filters.Any())
            SetFilters(settings.Filters);

        if (settings.PageNumber > 0)
        {
            if (settings.PageSize > 0 && settings.PageNumber < settings.PageSize)
            {
                gridCurrentState = new GridState<TItem>(settings.PageNumber, gridCurrentState.Sorting);
                this.pageSize = settings.PageSize;
            }
            else
            {
                gridCurrentState = new GridState<TItem>(1, null);
                this.pageSize = 10;
            }
        }
        else
        {
            gridCurrentState = new GridState<TItem>(1, null);
            this.pageSize = 10;
        }

    }

    /// <summary>
    /// Refresh the grid data.
    /// </summary>
    /// <returns>Task</returns>
    public async Task RefreshDataAsync(CancellationToken cancellationToken = default)
    {
        await RefreshDataAsync(false, cancellationToken);
    }

    internal async Task RefreshDataAsync(bool firstRender = false, CancellationToken cancellationToken = default)
    {
        if (requestInProgress)
            return;

        requestInProgress = true;

        if (firstRender)
            await LoadGridSettingsAsync();

        var request = new GridDataProviderRequest<TItem>
        {
            PageNumber = this.AllowPaging ? gridCurrentState.PageIndex : 0,
            PageSize = this.AllowPaging ? this.pageSize : 0,
            Sorting = this.AllowSorting ? (gridCurrentState.Sorting ?? GetDefaultSorting()) : null,
            Filters = this.AllowFiltering ? GetFilters() : null,
            CancellationToken = cancellationToken
        };

        GridDataProviderResult<TItem> result = default!;

        if (DataProvider is not null)
            result = await DataProvider.Invoke(request);
        else if (Data is not null)
            result = request.ApplyTo(Data);

        if (result is not null)
        {
            items = result.Data.ToList();
            totalCount = result.TotalCount ?? result.Data.Count();
        }
        else
        {
            items = new List<TItem> { };
            totalCount = 0;
        }

        if (AllowSelection)
            PrepareCheckboxIds();

        requestInProgress = false;

        await InvokeAsync(StateHasChanged);
    }

    private Task SaveGridSettingsAsync()
    {
        if (!GridSettingsChanged.HasDelegate)
            return Task.CompletedTask;

        var settings = new GridSettings
        {
            PageNumber = this.AllowPaging ? gridCurrentState.PageIndex : 0,
            PageSize = this.AllowPaging ? this.pageSize : 0,
            Filters = this.AllowFiltering ? GetFilters() : null
        };

        return GridSettingsChanged.InvokeAsync(settings);
    }

    /// <summary>
    /// Child selection template.
    /// </summary>
    private RenderFragment ChildSelectionTemplate(int rowIndex, TItem rowData)
    {
        return builder =>
        {
            // td > div "class" > input
            builder.OpenElement(101, "td");

            builder.OpenElement(102, "div");
            builder.AddAttribute(103, "class", $"form-check bb-grid-form-check-{headerCheckboxId}");

            builder.OpenElement(104, "input");
            builder.AddAttribute(105, "class", "form-check-input");
            builder.AddAttribute(106, "type", "checkbox");
            builder.AddAttribute(107, "role", "button");

            // disable the checkbox
            // remove the onchange event binding
            // add disabled attribute
            if (DisableRowSelection?.Invoke(rowData) ?? false)
            {
                builder.AddAttribute(108, "disabled", "disabled");
            }
            else
            {
                var id = checkboxIds[rowIndex];
                builder.AddAttribute(109, "id", id);
                builder.AddAttribute(110, "onchange", async (ChangeEventArgs args) => await OnRowCheckboxChanged(id, rowData, args));
                builder.AddEventStopPropagationAttribute(111, "onclick", true);
            }

            builder.CloseElement(); // close: input
            builder.CloseElement(); // close: div
            builder.CloseElement(); // close: th
        };
    }

    private void PrepareCheckboxIds()
    {
        checkboxIds = checkboxIds ?? new Dictionary<int, string>();
        var currentLength = checkboxIds.Count;
        var itemsCount = items.Count;
        if (currentLength < itemsCount)
        {
            var remaining = itemsCount - currentLength;
            for (int i = currentLength; i < itemsCount; i++)
            {
                checkboxIds[i] = IdGenerator.Generate;
            }
        }
    }

    private string GetPaginationItemsText()
    {
        var startRecord = (gridCurrentState.PageIndex - 1) * pageSize + 1;
        var endRecord = gridCurrentState.PageIndex * pageSize;

        if (endRecord > totalCount)
            endRecord = totalCount ?? 0;

        return string.Format(PaginationItemsTextFormat, startRecord, endRecord, totalCount);
    }

    #endregion Methods

    #region Properties

    /// <inheritdoc/>
    protected override bool ShouldAutoGenerateId => true;

    /// <summary>
    /// Gets or sets the grid delete.
    /// </summary>
    //[Parameter] public int AllowDelete { get; set; }

    /// <summary>
    /// Gets or sets the grid edit.
    /// </summary>
    //[Parameter] public int AllowEdit { get; set; }

    /// <summary>
    /// Gets or sets the grid filtering.
    /// </summary>
    [Parameter] public bool AllowFiltering { get; set; }

    /// <summary>
    /// Gets or sets the grid paging.
    /// </summary>
    [Parameter] public bool AllowPaging { get; set; }

    /// <summary>
    /// Gets or sets the grid selection.
    /// </summary>
    [Parameter] public bool AllowSelection { get; set; }

    /// <summary>
    /// Gets or sets the grid sorting.
    /// </summary>
    [Parameter] public bool AllowSorting { get; set; }

    /// <summary>
    /// Specifies the content to be rendered inside the grid.
    /// </summary>
    [Parameter] public RenderFragment ChildContent { get; set; } = default!;

    /// <summary>
    /// Gets or sets the grid data.
    /// </summary>
    [Parameter] public IEnumerable<TItem> Data { get; set; } = default!;

    /// <summary>
    /// DataProvider is for items to render. 
    /// The provider should always return an instance of 'GridDataProviderResult', and 'null' is not allowed.
    /// </summary>
    [Parameter] public GridDataProviderDelegate<TItem> DataProvider { get; set; } = default!;

    /// <summary>
    /// Enable or disable the header checkbox selection.
    /// </summary>
    [Parameter] public Func<IEnumerable<TItem>, bool>? DisableAllRowsSelection { get; set; }

    /// <summary>
    /// Enable or disable the row level checkbox selection.
    /// </summary>
    [Parameter] public Func<TItem, bool>? DisableRowSelection { get; set; }

    /// <summary>
    /// Template to render when there are no rows to display.
    /// </summary>
    public RenderFragment EmptyDataTemplate { get; set; } = default!;

    /// <summary>
    /// Shows text on no records.
    /// </summary>
    [Parameter] public string EmptyText { get; set; } = "No records to display";

    /// <summary>
    /// This event is fired when the grid state is changed.
    /// </summary>
    [Parameter] public EventCallback<GridSettings> GridSettingsChanged { get; set; }

    /// <summary>
    /// Header selection template.
    /// </summary>
    private RenderFragment HeaderSelectionTemplate
    {
        get
        {
            return headerSelectionTemplate ??= (builder =>
            {
                // th "style" > div "class" > input
                builder.OpenElement(101, "th");
                builder.AddAttribute(102, "style", "width: 2rem;");

                if (SelectionMode == GridSelectionMode.Multiple)
                {
                    builder.OpenElement(103, "div");
                    builder.AddAttribute(104, "class", "form-check");

                    builder.OpenElement(105, "input");
                    builder.AddAttribute(106, "class", "form-check-input");
                    builder.AddAttribute(107, "type", "checkbox");
                    builder.AddAttribute(108, "role", "button");

                    // disable the checkbox
                    // remove the onchange event binding
                    // add disabled attribute
                    if (DisableAllRowsSelection?.Invoke(items) ?? false)
                    {
                        builder.AddAttribute(109, "disabled", "disabled");
                    }
                    else
                    {
                        builder.AddAttribute(110, "id", headerCheckboxId);
                        builder.AddAttribute(111, "onchange", async (ChangeEventArgs args) => await OnHeaderCheckboxChanged(args));
                        builder.AddEventStopPropagationAttribute(112, "onclick", true);
                    }

                    builder.CloseElement(); // close: input
                    builder.CloseElement(); // close: div
                }

                builder.CloseElement(); // close: th
            });
        }
    }

    /// <summary>
    /// Gets or sets the page size of the grid.
    /// </summary>
    [Parameter] public int PageSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets the page size selector items.
    /// </summary>
    [Parameter, EditorRequired] public int[] PageSizeSelectorItems { get; set; } = new int[] { 10, 20, 50 };

    /// <summary>
    /// Gets or sets the page size selector visible.
    /// </summary>
    [Parameter] public bool PageSizeSelectorVisible { get; set; }

    [Obsolete("PaginationAlignment parameter is not supported from 1.8.0 version onwards")]
    /// <summary>
    /// Gets or sets the pagination alignment.
    /// </summary>
    [Parameter] public Alignment PaginationAlignment { get; set; } = Alignment.Start;

    /// <summary>
    /// Gets or sets the pagination items text format.
    /// </summary>
    [Parameter, EditorRequired] public string PaginationItemsTextFormat { get; set; } = "{0} - {1} of {2} items";

    /// <summary>
    /// Gets or sets the row class.
    /// </summary>
    [Parameter] public Func<TItem, string>? RowClass { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Grid is responsive.
    /// </summary>
    [Parameter] public bool Responsive { get; set; }

    /// <summary>
    /// This event is fired when the items selection changed.
    /// </summary>
    [Parameter] public EventCallback<HashSet<TItem>> SelectedItemsChanged { get; set; }

    /// <summary>
    /// Gets or sets the grid selection mode.
    /// </summary>
    [Parameter] public GridSelectionMode SelectionMode { get; set; } = GridSelectionMode.Single;

    /// <summary>
    /// Settings is for grid to render. 
    /// The provider should always return an instance of 'GridSettings', and 'null' is not allowed.
    /// </summary>
    [Parameter] public GridSettingsProviderDelegate SettingsProvider { get; set; } = default!;

    #endregion Properties
}
