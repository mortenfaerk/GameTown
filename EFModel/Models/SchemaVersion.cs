using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class SchemaVersion
{
    public int Version { get; set; }

    public DateTime AppliedAt { get; set; }
}
