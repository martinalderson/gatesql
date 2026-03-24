using System.ComponentModel.DataAnnotations;

namespace DbProxy.Data;

public class SettingsEntity
{
    [Key]
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
