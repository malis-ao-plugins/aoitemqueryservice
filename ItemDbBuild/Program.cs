// Builds the item database outside the game.
//   ItemDbBuild <game directory> <database path>
// Exit code 0 when the database is in place; otherwise the reason is on stderr.

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: ItemDbBuild <game directory> <database path>");
    return 2;
}

try
{
    ItemDbBuilder.Build(args[0], args[1], Console.WriteLine);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
