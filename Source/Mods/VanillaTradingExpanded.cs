using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Trading Expanded by Oskar Potocki and Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaTradingExpanded"/>
    [MpCompatFor("vanillaexpanded.vanillatradingexpanded")]
    public class VanillaTradingExpanded
    {
        private static Type managerType;
        private static Type bankType;
        private static Type companyType;
        private static Type contractType;
        private static Type transactionType;
        private static PropertyInfo managerInstance;
        private static PropertyInfo managerBanks;
        private static FieldInfo companiesField;
        private static FieldInfo npcContractsField;
        private static FieldInfo playerContractsField;
        private static FieldInfo bankFactionField;
        private static FieldInfo bankLoansField;
        private static FieldInfo bankExtensionField;
        private static FieldInfo loanOptionsField;
        private static FieldInfo companyFollowField;
        private static FieldInfo transactionTransferField;
        private static FieldInfo transactionSpendField;
        private static FieldInfo transactionCompaniesField;
        private static FieldInfo transactionPostActionField;

        public VanillaTradingExpanded(ModContentPack mod)
        {
            managerType = AccessTools.TypeByName("VanillaTradingExpanded.TradingManager");
            bankType = AccessTools.TypeByName("VanillaTradingExpanded.Bank");
            companyType = AccessTools.TypeByName("VanillaTradingExpanded.Company");
            contractType = AccessTools.TypeByName("VanillaTradingExpanded.Contract");
            transactionType = AccessTools.TypeByName("VanillaTradingExpanded.TransactionProcess");

            managerInstance = AccessTools.Property(managerType, "Instance");
            managerBanks = AccessTools.Property(managerType, "Banks");
            companiesField = AccessTools.Field(managerType, "companies");
            npcContractsField = AccessTools.Field(managerType, "npcSubmittedContracts");
            playerContractsField = AccessTools.Field(managerType, "playerSubmittedContracts");
            bankFactionField = AccessTools.Field(bankType, "parentFaction");
            bankLoansField = AccessTools.Field(bankType, "loans");
            bankExtensionField = AccessTools.Field(bankType, "bankExtension");
            loanOptionsField = AccessTools.Field(bankExtensionField.FieldType, "loanOptions");
            companyFollowField = AccessTools.Field(companyType, "playerFollowsNews");
            transactionTransferField = AccessTools.Field(transactionType, "amountToTransfer");
            transactionSpendField = AccessTools.Field(transactionType, "amountToSpend");
            transactionCompaniesField = AccessTools.Field(transactionType, "companySharesToBuyOrSell");
            transactionPostActionField = AccessTools.Field(transactionType, "postTransactionAction");

            MP.RegisterSyncWorker<object>(SyncBank, bankType);
            MP.RegisterSyncWorker<object>(SyncLoan, AccessTools.TypeByName("VanillaTradingExpanded.Loan"));
            MP.RegisterSyncWorker<object>(SyncLoanOption, AccessTools.TypeByName("VanillaTradingExpanded.LoanOption"));
            MP.RegisterSyncWorker<object>(SyncCompany, companyType);
            MP.RegisterSyncWorker<object>(SyncContract, contractType);
            MP.RegisterSyncWorker<object>(SyncManager, managerType);

            MP.RegisterSyncMethod(bankType, "DepositSilver");
            MP.RegisterSyncMethod(bankType, "WithdrawSilver");
            MP.RegisterSyncMethod(bankType, "TakeLoan");
            MP.RegisterSyncMethod(bankType, "TryRepayFromBalance");
            MP.RegisterSyncMethod(managerType, "CompleteContract");
            MP.RegisterSyncMethod(typeof(VanillaTradingExpanded), nameof(SyncedSubmitContract));
            MP.RegisterSyncMethod(typeof(VanillaTradingExpanded), nameof(SyncedCancelContract));
            MP.RegisterSyncMethod(typeof(VanillaTradingExpanded), nameof(SyncedFollowCompanyNews));
            MP.RegisterSyncMethod(typeof(VanillaTradingExpanded), nameof(SyncedStockTransaction));

            var contractsWindow = AccessTools.TypeByName("VanillaTradingExpanded.Window_Contracts");
            MpCompat.harmony.Patch(AccessTools.Method(contractsWindow, "DoWindowContents"),
                prefix: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(PreContractsWindow)),
                postfix: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(PostContractsWindow)));

            var stockWindow = AccessTools.TypeByName("VanillaTradingExpanded.Window_StockMarket");
            MpCompat.harmony.Patch(AccessTools.Method(stockWindow, "DoWindowContents"),
                prefix: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(PreStockWindow)),
                postfix: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(PostStockWindow)));

            MpCompat.harmony.Patch(AccessTools.Method(transactionType, "PerformTransaction"),
                prefix: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(PrePerformTransaction)));
        }

        private static object Manager => managerInstance.GetValue(null);
        private static IList Banks => (IList)managerBanks.GetValue(Manager);
        private static IList Companies => (IList)companiesField.GetValue(Manager);
        private static IList NpcContracts => (IList)npcContractsField.GetValue(Manager);
        private static IList PlayerContracts => (IList)playerContractsField.GetValue(Manager);

        private static void SyncManager(SyncWorker sync, ref object manager)
        {
            if (!sync.isWriting)
                manager = Manager;
        }

        private static void SyncBank(SyncWorker sync, ref object bank)
        {
            if (sync.isWriting)
                sync.Write((Faction)bankFactionField.GetValue(bank));
            else
            {
                var faction = sync.Read<Faction>();
                bank = Banks.Cast<object>().FirstOrDefault(x => bankFactionField.GetValue(x) == faction);
            }
        }

        private static void SyncLoan(SyncWorker sync, ref object loan)
        {
            if (sync.isWriting)
            {
                var loanToFind = loan;
                var bank = Banks.Cast<object>().FirstOrDefault(x => ((IList)bankLoansField.GetValue(x)).Contains(loanToFind));
                sync.Write(bank == null ? null : (Faction)bankFactionField.GetValue(bank));
                sync.Write(bank == null ? -1 : ((IList)bankLoansField.GetValue(bank)).IndexOf(loan));
            }
            else
            {
                var faction = sync.Read<Faction>();
                var index = sync.Read<int>();
                var bank = Banks.Cast<object>().FirstOrDefault(x => bankFactionField.GetValue(x) == faction);
                if (bank != null && index >= 0)
                    loan = ((IList)bankLoansField.GetValue(bank))[index];
            }
        }

        private static void SyncLoanOption(SyncWorker sync, ref object option)
        {
            if (sync.isWriting)
            {
                var optionToFind = option;
                var bank = Banks.Cast<object>().FirstOrDefault(x => ((IList)loanOptionsField.GetValue(bankExtensionField.GetValue(x))).Contains(optionToFind));
                sync.Write(bank == null ? null : (Faction)bankFactionField.GetValue(bank));
                sync.Write(bank == null ? -1 : ((IList)loanOptionsField.GetValue(bankExtensionField.GetValue(bank))).IndexOf(option));
            }
            else
            {
                var faction = sync.Read<Faction>();
                var index = sync.Read<int>();
                var bank = Banks.Cast<object>().FirstOrDefault(x => bankFactionField.GetValue(x) == faction);
                if (bank != null && index >= 0)
                    option = ((IList)loanOptionsField.GetValue(bankExtensionField.GetValue(bank)))[index];
            }
        }

        private static void SyncCompany(SyncWorker sync, ref object company)
        {
            if (sync.isWriting)
                sync.Write(Companies.IndexOf(company));
            else
            {
                var index = sync.Read<int>();
                if (index >= 0 && index < Companies.Count)
                    company = Companies[index];
            }
        }

        private static void SyncContract(SyncWorker sync, ref object contract)
        {
            if (sync.isWriting)
            {
                var npcIndex = NpcContracts.IndexOf(contract);
                sync.Write(npcIndex >= 0);
                sync.Write(npcIndex >= 0 ? npcIndex : PlayerContracts.IndexOf(contract));
            }
            else
            {
                var list = sync.Read<bool>() ? NpcContracts : PlayerContracts;
                var index = sync.Read<int>();
                if (index >= 0 && index < list.Count)
                    contract = list[index];
            }
        }

        private sealed class ContractWindowState
        {
            public object[] Contracts;
        }

        private static void PreContractsWindow(ref ContractWindowState __state)
        {
            if (MP.IsInMultiplayer && !MP.IsExecutingSyncCommand)
                __state = new ContractWindowState { Contracts = PlayerContracts.Cast<object>().ToArray() };
        }

        private static void PostContractsWindow(ContractWindowState __state)
        {
            if (__state == null)
                return;

            var before = __state.Contracts;
            var current = PlayerContracts.Cast<object>().ToList();
            var submitted = current.FirstOrDefault(x => !before.Contains(x));
            if (submitted != null)
            {
                PlayerContracts.Remove(submitted);
                SyncedSubmitContract(
                    (ThingDef)AccessTools.Field(contractType, "item").GetValue(submitted),
                    (ThingDef)AccessTools.Field(contractType, "stuff").GetValue(submitted),
                    (int)AccessTools.Field(contractType, "amount").GetValue(submitted),
                    (int)AccessTools.Field(contractType, "reward").GetValue(submitted),
                    (float)AccessTools.Field(contractType, "rewardAsFloat").GetValue(submitted),
                    (int)AccessTools.Field(contractType, "expiresInTicks").GetValue(submitted));
            }

            for (var i = 0; i < before.Length; i++)
            {
                if (current.Contains(before[i]))
                    continue;
                PlayerContracts.Insert(Math.Min(i, PlayerContracts.Count), before[i]);
                SyncedCancelContract(i);
                break;
            }
        }

        private static void SyncedSubmitContract(ThingDef item, ThingDef stuff, int amount, int reward, float rewardAsFloat, int expiresInTicks)
        {
            var contract = Activator.CreateInstance(contractType);
            AccessTools.Field(contractType, "item").SetValue(contract, item);
            AccessTools.Field(contractType, "stuff").SetValue(contract, stuff);
            AccessTools.Field(contractType, "amount").SetValue(contract, amount);
            AccessTools.Field(contractType, "reward").SetValue(contract, reward);
            AccessTools.Field(contractType, "rewardAsFloat").SetValue(contract, rewardAsFloat);
            AccessTools.Field(contractType, "expiresInTicks").SetValue(contract, expiresInTicks);
            PlayerContracts.Add(contract);
        }

        private static void SyncedCancelContract(int index)
        {
            if (index >= 0 && index < PlayerContracts.Count)
                PlayerContracts.RemoveAt(index);
        }

        private static void PreStockWindow(ref bool[] __state)
        {
            __state = MP.IsInMultiplayer && !MP.IsExecutingSyncCommand
                ? Companies.Cast<object>().Select(x => (bool)companyFollowField.GetValue(x)).ToArray()
                : null;
        }

        private static void PostStockWindow(bool[] __state)
        {
            if (__state == null)
                return;
            for (var i = 0; i < __state.Length && i < Companies.Count; i++)
            {
                var company = Companies[i];
                var value = (bool)companyFollowField.GetValue(company);
                if (value == __state[i])
                    continue;
                companyFollowField.SetValue(company, __state[i]);
                SyncedFollowCompanyNews(i, value);
            }
        }

        private static void SyncedFollowCompanyNews(int companyIndex, bool value)
        {
            if (companyIndex >= 0 && companyIndex < Companies.Count)
                companyFollowField.SetValue(Companies[companyIndex], value);
        }

        private static bool PrePerformTransaction(object __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand || transactionPostActionField.GetValue(__instance) != null)
                return true;

            ExtractDictionary(transactionCompaniesField.GetValue(__instance), Companies, out var companyIndices, out var companyAmounts);
            ExtractBanks(transactionTransferField.GetValue(__instance), out var transferFactions, out var transferAmounts);
            ExtractBanks(transactionSpendField.GetValue(__instance), out var spendFactions, out var spendAmounts);
            SyncedStockTransaction(companyIndices, companyAmounts, transferFactions, transferAmounts, spendFactions, spendAmounts);
            return false;
        }

        private static void ExtractDictionary(object dictionary, IList keys, out int[] indices, out int[] amounts)
        {
            if (dictionary == null)
            {
                indices = Array.Empty<int>();
                amounts = Array.Empty<int>();
                return;
            }
            var entries = ((IDictionary)dictionary).Cast<DictionaryEntry>().Where(x => (int)x.Value != 0).ToArray();
            indices = entries.Select(x => keys.IndexOf(x.Key)).ToArray();
            amounts = entries.Select(x => (int)x.Value).ToArray();
        }

        private static void ExtractBanks(object dictionary, out Faction[] factions, out int[] amounts)
        {
            if (dictionary == null)
            {
                factions = Array.Empty<Faction>();
                amounts = Array.Empty<int>();
                return;
            }
            var entries = ((IDictionary)dictionary).Cast<DictionaryEntry>().Where(x => (int)x.Value != 0).ToArray();
            factions = entries.Select(x => (Faction)bankFactionField.GetValue(x.Key)).ToArray();
            amounts = entries.Select(x => (int)x.Value).ToArray();
        }

        private static void SyncedStockTransaction(int[] companyIndices, int[] companyAmounts,
            Faction[] transferFactions, int[] transferAmounts, Faction[] spendFactions, int[] spendAmounts)
        {
            var transaction = Activator.CreateInstance(transactionType);
            var companyChanges = (IDictionary)transactionCompaniesField.GetValue(transaction);
            for (var i = 0; i < companyIndices.Length; i++)
                if (companyIndices[i] >= 0 && companyIndices[i] < Companies.Count)
                    companyChanges[Companies[companyIndices[i]]] = companyAmounts[i];

            transactionTransferField.SetValue(transaction, MakeBankDictionary(transferFactions, transferAmounts));
            transactionSpendField.SetValue(transaction, MakeBankDictionary(spendFactions, spendAmounts));
            AccessTools.Method(transactionType, "PerformTransaction").Invoke(transaction, null);
        }

        private static object MakeBankDictionary(Faction[] factions, int[] amounts)
        {
            var dictionaryType = typeof(Dictionary<,>).MakeGenericType(bankType, typeof(int));
            var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType);
            for (var i = 0; i < factions.Length; i++)
            {
                var bank = Banks.Cast<object>().FirstOrDefault(x => bankFactionField.GetValue(x) == factions[i]);
                if (bank != null)
                    dictionary[bank] = amounts[i];
            }
            return dictionary;
        }
    }
}
