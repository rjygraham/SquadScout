using System.Collections;
using System.Reflection;

namespace SquadScout.Broker.Orleans;

public static class OrleansSqliteCompatibilityShim
{
    private const string PersistenceAssemblyName = "Orleans.Persistence.AdoNet";
    private const string StorageNamespace = "Orleans.Persistence.AdoNet.Storage";
    private const string SqliteInvariantAlias = "System.Data.SQLite";
    private const string SqliteInvariantDriver = "Microsoft.Data.Sqlite";

    public static OrleansSqliteCompatibilityResult EnsureConfigured(string configuredInvariant)
    {
        var assembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, PersistenceAssemblyName, StringComparison.Ordinal))
            ?? Assembly.Load(PersistenceAssemblyName);

        var invariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            configuredInvariant,
            SqliteInvariantAlias,
            SqliteInvariantDriver
        };

        var dbConstantsApplied = PatchDbConstantsStore(assembly, invariants);
        var providerFactoryApplied = PatchProviderFactoryMap(assembly, invariants);
        var invariantListApplied = PatchInvariantList(assembly, invariants);

        return new OrleansSqliteCompatibilityResult
        {
            ConfiguredInvariant = configuredInvariant,
            Applied = dbConstantsApplied || providerFactoryApplied || invariantListApplied,
            Note = dbConstantsApplied || providerFactoryApplied || invariantListApplied
                ? "Applied the local SQLite compatibility shim so Orleans ADO.NET storage recognizes Microsoft.Data.Sqlite for this single-silo broker."
                : "SQLite invariant support was already available in the Orleans ADO.NET provider."
        };
    }

    private static bool PatchDbConstantsStore(Assembly assembly, IReadOnlyCollection<string> invariants)
    {
        var storeType = assembly.GetType($"{StorageNamespace}.DbConstantsStore", throwOnError: true)
                        ?? throw new InvalidOperationException("Unable to locate Orleans DbConstantsStore.");
        var constantsType = assembly.GetType($"{StorageNamespace}.DbConstants", throwOnError: true)
                            ?? throw new InvalidOperationException("Unable to locate Orleans DbConstants.");
        var noOpInterceptorType = assembly.GetType($"{StorageNamespace}.NoOpCommandInterceptor", throwOnError: true)
                                  ?? throw new InvalidOperationException("Unable to locate Orleans NoOpCommandInterceptor.");

        var dictionaryField = storeType.GetField("invariantNameToConsts", BindingFlags.NonPublic | BindingFlags.Static)
                              ?? throw new InvalidOperationException("Unable to locate Orleans invariantNameToConsts field.");
        var dictionary = dictionaryField.GetValue(null) as IDictionary
                         ?? throw new InvalidOperationException("Unable to read Orleans invariantNameToConsts map.");

        var instanceMember =
            (MemberInfo?)noOpInterceptorType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? noOpInterceptorType.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to locate Orleans NoOpCommandInterceptor.Instance.");

        var interceptorInstance = instanceMember switch
        {
            PropertyInfo propertyInfo => propertyInfo.GetValue(null),
            FieldInfo fieldInfo => fieldInfo.GetValue(null),
            _ => null
        } ?? throw new InvalidOperationException("Unable to read Orleans NoOpCommandInterceptor.Instance.");

        var constructor = constantsType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(constructorInfo =>
            {
                var parameters = constructorInfo.GetParameters();
                return parameters.Length == 7;
            });

        var applied = false;
        foreach (var invariant in invariants)
        {
            if (dictionary.Contains(invariant))
            {
                continue;
            }

            var constants = constructor.Invoke(
            [
                '"',
                '"',
                " UNION ALL SELECT ",
                false,
                false,
                true,
                interceptorInstance
            ]);

            dictionary.Add(invariant, constants);
            applied = true;
        }

        return applied;
    }

    private static bool PatchProviderFactoryMap(Assembly assembly, IReadOnlyCollection<string> invariants)
    {
        var factoryType = assembly.GetType($"{StorageNamespace}.DbConnectionFactory", throwOnError: true)
                          ?? throw new InvalidOperationException("Unable to locate Orleans DbConnectionFactory.");
        var field = factoryType.GetField("providerFactoryTypeMap", BindingFlags.NonPublic | BindingFlags.Static)
                   ?? throw new InvalidOperationException("Unable to locate Orleans providerFactoryTypeMap field.");
        var dictionary = field.GetValue(null) as IDictionary
                         ?? throw new InvalidOperationException("Unable to read Orleans providerFactoryTypeMap.");

        var tupleType = typeof(Tuple<string, string>);
        var listType = typeof(List<>).MakeGenericType(tupleType);
        var tuple = Activator.CreateInstance(tupleType, SqliteInvariantDriver, "Microsoft.Data.Sqlite.SqliteFactory")
                    ?? throw new InvalidOperationException("Unable to create Orleans SQLite provider factory tuple.");

        var applied = false;
        foreach (var invariant in invariants)
        {
            if (dictionary.Contains(invariant))
            {
                continue;
            }

            var providerList = Activator.CreateInstance(listType)
                               ?? throw new InvalidOperationException("Unable to create Orleans SQLite provider factory list.");
            listType.GetMethod("Add")!.Invoke(providerList, [tuple]);
            dictionary.Add(invariant, providerList);
            applied = true;
        }

        return applied;
    }

    private static bool PatchInvariantList(Assembly assembly, IReadOnlyCollection<string> invariants)
    {
        var invariantsType = assembly.GetType($"{StorageNamespace}.AdoNetInvariants", throwOnError: false);
        if (invariantsType is null)
        {
            return false;
        }

        var property = invariantsType.GetProperty("Invariants", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property?.GetValue(null) is not IList invariantList)
        {
            return false;
        }

        var applied = false;
        foreach (var invariant in invariants)
        {
            var alreadyPresent = invariantList.Cast<object?>().Any(entry => string.Equals(entry?.ToString(), invariant, StringComparison.OrdinalIgnoreCase));
            if (alreadyPresent)
            {
                continue;
            }

            invariantList.Add(invariant);
            applied = true;
        }

        return applied;
    }
}

public sealed class OrleansSqliteCompatibilityResult
{
    public required string ConfiguredInvariant { get; init; }

    public bool Applied { get; init; }

    public required string Note { get; init; }
}
