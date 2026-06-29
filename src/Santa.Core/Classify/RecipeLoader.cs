using Santa.Core;

namespace Santa.Core.Classify;

public sealed class RecipeLoader
{
    public string Root { get; }

    public RecipeLoader(string root) => Root = root;

    public static string DefaultRoot => SantaPaths.RecipesDir;

    public IReadOnlyList<ClassificationRecipe> LoadAll()
    {
        if (!Directory.Exists(Root)) return Array.Empty<ClassificationRecipe>();
        var list = new List<ClassificationRecipe>();
        foreach (var path in Directory.EnumerateFiles(Root, "*.yaml"))
        {
            try { list.Add(ClassificationRecipe.LoadFromFile(path)); }
            catch (Exception ex) { Console.Error.WriteLine($"  ! recipe {Path.GetFileName(path)}: {ex.Message}"); }
        }
        return list;
    }

    public ClassificationRecipe? Find(string id)
    {
        var path = Path.Combine(Root, id + ".yaml");
        return File.Exists(path) ? ClassificationRecipe.LoadFromFile(path) : null;
    }
}
