namespace ChillHub.Core.Home {
    internal static class MissingGamesText {
        internal const string Title = "Этих игр больше нет в каталоге";
        internal const string Description = "Их файлы остались на компьютере. Выберите папки, которые хотите удалить.";
        internal const string Warning = "Выбранные папки будут удалены безвозвратно вместе со всем содержимым, включая сохранения внутри них.";
        internal const string Later = "Позже";
        internal const string Delete = "Удалить выбранные";
        internal const string Close = "Закрыть";
        internal const string Deleted = "Папка удалена";
        internal const string Failed = "Не удалось удалить всё содержимое. Закройте игру и программы, использующие эту папку.";
        internal const string Busy = "Игра используется или обрабатывается лаунчером. Попробуйте при следующем запуске.";
        internal const string Changed = "Папка игр изменилась. Удаление пропущено.";
        internal const string Available = "Игра снова есть в каталоге. Удаление пропущено.";
        internal const string Finished = "Удаление завершено. Оставшиеся папки можно удалить при следующем запуске.";

        internal static string Progress(int current, int total) => $"Удаление папок: {current} из {total}";
    }
}
