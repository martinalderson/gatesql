using System.ComponentModel.DataAnnotations;

namespace DbProxy.Data;

public class SchemaAnnotationEntity
{
    [Key]
    public required string TableName { get; set; }
    public string? Description { get; set; }
    public string? ExampleQueries { get; set; }
    public string? Notes { get; set; }
}
