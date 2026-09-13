using Shittim.Services.Client;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.FlatData;
using Schale.Excel;

namespace Shittim.Commands
{
    [CommandHandler("max", "Max out character stats and equipment", "!max <all|charactername>")]
    internal class MaxCommand : Command
    {
        public MaxCommand(IClientConnection connection, string[] args, bool validate = true) : base(connection, args, validate) { }

        [Argument(0, @"^.+$", "Character name or 'all'")]
        public string CharacterName { get; set; } = "all";

        public override async Task Execute()
        {
            using var context = await connection.Context.CreateDbContextAsync();
            var characterExcel = connection.ExcelTableService.GetTable<CharacterExcelT>();
            var all = CharacterName.Equals("all", StringComparison.OrdinalIgnoreCase);
            var characters = context.Characters
                .Where(x => x.AccountServerId == connection.AccountServerId).ToList();

            if (!all)
            {
                var data = characterExcel.FirstOrDefault(x =>
                    x.DevName != null && x.DevName.Contains(CharacterName, StringComparison.OrdinalIgnoreCase));
                if (data == null)
                {
                    await connection.SendChatMessage($"Character '{CharacterName}' not found!");
                    return;
                }
                characters = characters.Where(x => x.UniqueId == data.Id).ToList();
                if (characters.Count == 0)
                {
                    await connection.SendChatMessage($"You don't own {data.DevName}!");
                    return;
                }
            }
            if (characters.Count == 0)
            {
                await connection.SendChatMessage("You don't own any characters!");
                return;
            }

            // Equipment IDs are allocated by SaveChanges; keep both the rows and slot
            // bindings in one transaction so a failure cannot leave a half-maxed roster.
            await using var transaction = await context.Database.BeginTransactionAsync();
            var count = 0;
            var bindEquipment = new List<Action>();
            foreach (var character in characters)
            {
                var data = characterExcel.FirstOrDefault(x => x.Id == character.UniqueId);
                if (data == null) continue;
                bindEquipment.Add(MaxCharacter(context, character, data));
                count++;
            }
            await context.SaveChangesAsync();
            foreach (var bind in bindEquipment) bind();
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            await connection.SendChatMessage($"Maxed out {count} characters, including skills, bond, potential, weapons, equipment and unique gear!");
        }

        private Action MaxCharacter(SchaleDataContext context, CharacterDBServer character, CharacterExcelT data)
        {
            var excel = connection.ExcelTableService;
            character.Level = excel.GetTable<CharacterLevelExcelT>().Max(x => x.Level);
            character.Exp = 0;
            character.StarGrade = data.MaxStarGrade;
            character.FavorRank = 100;
            character.FavorExp = 0;
            character.PublicSkillLevel = 10;
            character.ExSkillLevel = 5;
            character.PassiveSkillLevel = 10;
            character.ExtraPassiveSkillLevel = 10;
            character.PotentialStats = new Dictionary<int, int> { { 1, 25 }, { 2, 25 }, { 3, 25 } };

            var weaponData = excel.GetTable<CharacterWeaponExcelT>().FirstOrDefault(x => x.Id == character.UniqueId);
            var weaponStars = weaponData?.Unlock.TakeWhile(x => x).Count() ?? 0;
            if (weaponStars > 0)
            {
                var weapon = context.Weapons.FirstOrDefault(x => x.AccountServerId == character.AccountServerId
                    && x.UniqueId == character.UniqueId);
                if (weapon == null)
                {
                    weapon = new WeaponDBServer { AccountServerId = character.AccountServerId, UniqueId = character.UniqueId };
                    context.Weapons.Add(weapon);
                }
                weapon.BoundCharacterServerId = character.ServerId;
                weapon.StarGrade = weaponStars;
                weapon.Level = weaponData.MaxLevel[weaponStars - 1];
                weapon.Exp = 0;
            }

            var equipmentTable = excel.GetTable<EquipmentExcelT>().GetCharacterEquipment();
            var boundEquipment = context.Equipments.Where(x => x.AccountServerId == character.AccountServerId
                && x.BoundCharacterServerId == character.ServerId).ToList();
            var equipment = new List<EquipmentDBServer>();
            for (var slot = 0; slot < data.EquipmentSlot.Count; slot++)
            {
                var category = data.EquipmentSlot[slot];
                var template = equipmentTable.Where(x => x.EquipmentCategory == category)
                    .OrderByDescending(x => x.TierInit).ThenByDescending(x => x.MaxLevel).First();
                var slotId = character.EquipmentServerIds?.ElementAtOrDefault(slot) ?? 0;
                var item = boundEquipment.FirstOrDefault(x => x.ServerId == slotId && !equipment.Contains(x))
                    ?? boundEquipment.FirstOrDefault(x => !equipment.Contains(x)
                        && equipmentTable.Any(t => t.Id == x.UniqueId && t.EquipmentCategory == category));
                if (item == null)
                {
                    item = new EquipmentDBServer { AccountServerId = character.AccountServerId, BoundCharacterServerId = character.ServerId };
                    context.Equipments.Add(item);
                }
                item.UniqueId = template.Id;
                item.Tier = (int)template.TierInit;
                item.Level = template.MaxLevel;
                item.Exp = 0;
                item.StackCount = 1;
                equipment.Add(item);
            }

            var gearData = excel.GetTable<CharacterGearExcelT>().Where(x => x.CharacterId == character.UniqueId)
                .OrderByDescending(x => x.Tier).FirstOrDefault();
            var gear = context.Gears.FirstOrDefault(x => x.AccountServerId == character.AccountServerId
                && x.BoundCharacterServerId == character.ServerId);
            if (gearData != null)
            {
                if (gear == null)
                {
                    gear = new GearDBServer { AccountServerId = character.AccountServerId, BoundCharacterServerId = character.ServerId };
                    context.Gears.Add(gear);
                }
                gear.UniqueId = gearData.Id;
                gear.Tier = (int)gearData.Tier;
                gear.Level = (int)gearData.MaxLevel;
                gear.Exp = 0;
                gear.SlotIndex = 4;
            }

            return () =>
            {
                character.EquipmentServerIds = equipment.Select(x => x.ServerId).ToList();
                if (gear != null) character.EquipmentServerIds.Add(gear.ServerId);
            };
        }
    }
}
