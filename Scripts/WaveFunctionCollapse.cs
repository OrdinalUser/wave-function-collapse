using Godot;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

public partial class WaveFunctionCollapse : Node2D
{
    public struct TilePrototype
    {
        public string sprite;
        public string name;
        public float weight;
        public string[] northConnections;
        public string[] westConnections;
        public string[] eastConnections;
        public Vector2I atlasCoords;
    }

    public struct Config
    {
        public int seed;
        public int columns;
        public int rows;
        public int spriteSize;
    }

    public struct Tile
    {
        public int tileId; // index to all known tiles
        public bool collapsed;
        public List<int> options; // holds ids of all possible tiles this tile can become

        public int GetEntropy() { return options.Count; }
        public Tile(int optionCount)
        {
            tileId = -1;
            collapsed = false;
            options = new List<int>();
            for (int i = 0; i < optionCount; i++)
            {
                options.Add(i);
            }
        }

        public Tile(int tileId, List<int> options, bool collapsed)
        {
            this.tileId = tileId;
            this.collapsed = collapsed;
            this.options = options;
        }
    }

    Config m_Config;

    [Export] public string RulesetFilePath = "res://Rules.json";

    public Tile[,] m_Tiles;
    public TilePrototype[] m_KnownTiles;
    public Dictionary<string, int> m_NamedKnownTiles;

    public bool m_Finished;

    private Random m_Random;

    private TileMapLayer m_TileMapLayer;
    private TileSet m_TileSet;
    private Image m_Spritesheet;
    private ImageTexture m_AtlasTexture;
    private TileSetAtlasSource m_TileSetAtlasSource;

    bool LoadConfig()
    {
        if (!FileAccess.FileExists(RulesetFilePath))
        {
            GD.PushError("Config file not found!");
            return false;
        }

        FileAccess configFile = FileAccess.Open(RulesetFilePath, FileAccess.ModeFlags.Read);

        Json json = new Json();
        var err = json.Parse(configFile.GetAsText());
        configFile.Close();
        if (err != Error.Ok)
        {
            GD.PushError("Failed to parse config!");
            return false;
        }

        m_NamedKnownTiles = new Dictionary<string, int>();

        using var config = json.Data.AsGodotDictionary();
        List<TilePrototype> tilePrototypes = new List<TilePrototype>();
        foreach (var tileName in config["Tiles"].AsStringArray())
        {
            using var tileData = config[tileName].AsGodotDictionary();
            TilePrototype tile = new TilePrototype();

            tile.name = tileName;
            tile.weight = (float)tileData["Weight"];
            tile.sprite = (string)tileData["Sprite"];

            tile.northConnections = tileData["North"].AsStringArray();
            tile.eastConnections = tileData["East"].AsStringArray();
            tile.southConnections = tileData["South"].AsStringArray();
            tile.westConnections = tileData["West"].AsStringArray();

            m_NamedKnownTiles[tileName] = tilePrototypes.Count;
            tilePrototypes.Add(tile);
        }
        m_KnownTiles = tilePrototypes.ToArray();

        m_Config = new Config();
        var meta = config["Meta"].AsGodotDictionary();
        m_Config.spriteSize = meta["SpriteSize"].AsInt32();
        m_Config.seed = meta["Seed"].AsInt32();
        m_Config.rows = meta["Rows"].AsInt32();
        m_Config.columns = meta["Columns"].AsInt32();

        return true;
    }

    bool SetupTileMap()
    {
        int atlasSize = Mathf.CeilToInt(Mathf.Sqrt(m_KnownTiles.Length));
        int atlasDimension = atlasSize * m_Config.spriteSize;

        m_Spritesheet = new Image();
        m_Spritesheet = Image.CreateEmpty(atlasDimension, atlasDimension, false, Image.Format.Rgba8);

        int sizePerPicture = m_Config.spriteSize ^ 2 * 4;
        byte[] stitchedData = new byte[atlasDimension * atlasDimension * 4];

        int pictureIndex = 0;

        foreach (var tile in m_KnownTiles)
        {
            Image sprite = Image.LoadFromFile("res://Sprites/" + tile.sprite);
            if (sprite.GetFormat() != Image.Format.Rgba8)
            {
                GD.PushError("Unsupported format. Use only .png");
                return false;
            }

            byte[] spriteData = sprite.GetData();
            for (global::System.Int32 i = 0; i < spriteData.Length; i += 4)
            {
                int pixelI = i / 4;
                int pixelIndex = ((pixelI / m_Config.spriteSize) * atlasDimension) + (pixelI % m_Config.spriteSize); // i=40 == 40 : Good =(floor(40/64)*192) + (40 % 64)
                pixelIndex += (pictureIndex % atlasSize * m_Config.spriteSize) + (pictureIndex / atlasSize * m_Config.spriteSize * atlasDimension); // Picture index=4 == =(2%64*64) + (floor(2/3)*64*192)
                stitchedData[pixelIndex * 4 + 0] = spriteData[i + 0];
                stitchedData[pixelIndex * 4 + 1] = spriteData[i + 1];
                stitchedData[pixelIndex * 4 + 2] = spriteData[i + 2];
                stitchedData[pixelIndex * 4 + 3] = spriteData[i + 3];
            }
            pictureIndex++;
        }

        m_Spritesheet.SetData(atlasDimension, atlasDimension, false, Image.Format.Rgba8, stitchedData);
        m_Spritesheet.SavePng("res://Temp/output.png");

        m_AtlasTexture = new ImageTexture();
        m_AtlasTexture.SetImage(m_Spritesheet);

        m_TileSetAtlasSource = new TileSetAtlasSource();
        m_TileSetAtlasSource.ResourceName = "Tile Atlas Source";
        m_TileSetAtlasSource.Texture = m_AtlasTexture;
        m_TileSetAtlasSource.TextureRegionSize = new Vector2I(m_Config.spriteSize, m_Config.spriteSize);
        m_TileSetAtlasSource.UseTexturePadding = false;

        for (int i = 0; i < m_KnownTiles.Length; i++)
        {
            m_KnownTiles[i].atlasCoords = new Vector2I(i % atlasSize, i / atlasSize);
            m_TileSetAtlasSource.CreateTile(m_KnownTiles[i].atlasCoords);
            GD.Print("Created tile: { Id: " + i + ", name: " + m_KnownTiles[i].name + ", atlas: " + m_KnownTiles[i].atlasCoords + "}");
        }

        m_TileSet = new TileSet();
        m_TileSet.AddSource(m_TileSetAtlasSource, 0);
        m_TileSet.ResourceName = "Tile Atlas";
        m_TileSet.TileSize = new Vector2I(m_Config.spriteSize, m_Config.spriteSize);

        m_TileMapLayer = new TileMapLayer();
        m_TileMapLayer.Transform.Translated(new Vector2(50, 50));
        m_TileMapLayer.TileSet = m_TileSet;
        m_TileMapLayer.NavigationEnabled = false;
        m_TileMapLayer.CollisionEnabled = false;

        m_Random = new Random(m_Config.seed);

        AddChild(m_TileMapLayer);

        return true;
    }

    public override void _Ready()
    {
        m_Finished = false;
        if (!LoadConfig()) { GD.PrintErr("Failed to load config."); return; }
        if (!SetupTileMap()) { GD.PrintErr("Failed to build tiles."); return; }

        m_Tiles = new Tile[m_Config.columns, m_Config.rows];
        for (int y = 0; y < m_Config.rows; y++)
        {
            for (int x = 0; x < m_Config.columns; x++)
            {
                m_Tiles[x, y] = new Tile(m_KnownTiles.Length);
            }
        }

        //int cnt = m_KnownTiles.Length;
        //for (int i = 0; i < m_Config.columns; i++)
        //{
        //    for (int j = 0; j < m_Config.rows; j++)
        //    {
        //        m_TileMapLayer.SetCell(new Vector2I(i, j), 0, m_KnownTiles[(i % cnt + j * cnt) % cnt].atlasCoords);
        //    }
        //}
    }

    public override void _Process(double delta)
    {
        Cycle();
    }

    private bool Reduce(Vector2I cellPos, Direction fromWhere)
    {
        int x = cellPos.X, y = cellPos.Y;
        if (x < 0 || y < 0 || x > m_Config.columns || y > m_Config.rows) return false;
        bool changed = false;

        ref Tile tile = ref m_Tiles[x, y];
        List<int> newOptions = tile.options;

        if (x > 0)
        {
            List<int> directionOptions = new List<int>();
            ref Tile checkedTile = ref m_Tiles[x - 1, y];

            if (checkedTile.collapsed)
            {
                foreach (string optionName in m_KnownTiles[checkedTile.tileId].eastConnections)
                {
                    directionOptions.Add(m_NamedKnownTiles[optionName]);
                }
            }
            else
            {
                foreach (int option in checkedTile.options)
                {
                    List<int> tileOptions = new List<int>();
                    foreach (string optionName in m_KnownTiles[option].eastConnections)
                    {
                        tileOptions.Add(m_NamedKnownTiles[optionName]);
                    }
                    directionOptions = directionOptions.Union(tileOptions).ToList();
                }
            }
            newOptions = newOptions.Intersect(directionOptions).ToList();
        }
        if (y > 0)
        {
            List<int> directionOptions = new List<int>();
            ref Tile checkedTile = ref m_Tiles[x, y - 1];

            if (checkedTile.collapsed)
            {
                foreach (string optionName in m_KnownTiles[checkedTile.tileId].southConnections)
                {
                    directionOptions.Add(m_NamedKnownTiles[optionName]);
                }
            }
            else
            {
                foreach (int option in checkedTile.options)
                {
                    List<int> tileOptions = new List<int>();
                    foreach (string optionName in m_KnownTiles[option].southConnections)
                    {
                        tileOptions.Add(m_NamedKnownTiles[optionName]);
                    }
                    directionOptions = directionOptions.Union(tileOptions).ToList();
                }
            }
            newOptions = newOptions.Intersect(directionOptions).ToList();
        }
        if (x < m_Config.columns - 1)
        {
            List<int> directionOptions = new List<int>();
            ref Tile checkedTile = ref m_Tiles[x + 1, y];

            if (checkedTile.collapsed)
            {
                foreach (string optionName in m_KnownTiles[checkedTile.tileId].westConnections)
                {
                    directionOptions.Add(m_NamedKnownTiles[optionName]);
                }
            }
            else
            {
                foreach (int option in checkedTile.options)
                {
                    List<int> tileOptions = new List<int>();
                    foreach (string optionName in m_KnownTiles[option].westConnections)
                    {
                        tileOptions.Add(m_NamedKnownTiles[optionName]);
                    }
                    directionOptions = directionOptions.Union(tileOptions).ToList();
                }
            }
            newOptions = newOptions.Intersect(directionOptions).ToList();
        }
        if (y < m_Config.rows - 1)
        {
            List<int> directionOptions = new List<int>();
            ref Tile checkedTile = ref m_Tiles[x, y + 1];

            if (checkedTile.collapsed)
            {
                foreach (string optionName in m_KnownTiles[checkedTile.tileId].northConnections)
                {
                    directionOptions.Add(m_NamedKnownTiles[optionName]);
                }
            }
            else
            {
                foreach (int option in checkedTile.options)
                {
                    List<int> tileOptions = new List<int>();
                    foreach (string optionName in m_KnownTiles[option].northConnections)
                    {
                        tileOptions.Add(m_NamedKnownTiles[optionName]);
                    }
                    directionOptions = directionOptions.Union(tileOptions).ToList();
                }
            }
            newOptions = newOptions.Intersect(directionOptions).ToList();
        }

        if (tile.options.Count != newOptions.Count)
        {
            changed = true;
            m_Tiles[x, y].options = newOptions;
        }

        return changed;
    }

    enum Direction
    {
        NONE, UP, DOWN, LEFT, RIGHT
    }
    private void Constrain(Vector2I cellPos, Direction fromWhere)
    {
        int x = cellPos.X, y = cellPos.Y;

        if (x > 0 && fromWhere != Direction.LEFT)
        {
            if (Reduce(cellPos - new Vector2I(1, 0), Direction.RIGHT))
                Constrain(cellPos - new Vector2I(1, 0), Direction.RIGHT);
        }
        if (y > 0 && fromWhere != Direction.UP)
        {
            if (Reduce(cellPos - new Vector2I(0, 1), Direction.DOWN))
                Constrain(cellPos - new Vector2I(0, 1), Direction.DOWN);
        }
        if (x < m_Config.columns - 1 && fromWhere != Direction.RIGHT)
        {
            if (Reduce(cellPos + new Vector2I(1, 0), Direction.LEFT))
                Constrain(cellPos + new Vector2I(1, 0), Direction.LEFT);
        }
        if (y < m_Config.rows - 1 && fromWhere != Direction.DOWN)
        {
            if (Reduce(cellPos + new Vector2I(0, 1), Direction.UP))
                Constrain(cellPos + new Vector2I(0, 1), Direction.UP);
        }
    }

    private Vector2I Collapse()
    {
        List<Tuple<int, Vector2I>> entropyList = new List<Tuple<int, Vector2I>>();
        for (int y = 0; y < m_Config.rows; y++)
        {
            for (int x = 0; x < m_Config.columns; x++)
            {
                if (!m_Tiles[x, y].collapsed) entropyList.Add(new Tuple<int, Vector2I>(m_Tiles[x, y].GetEntropy(), new Vector2I(x, y)));
            }
        }
        if (entropyList.Count == 0) { m_Finished = true; GD.Print("Finished generating"); return new Vector2I(-1, -1); }

        List<Tuple<int, Vector2I>> sortedEntropy = entropyList.OrderBy(o => o.Item1).ToList();
        List<Tuple<int, Vector2I>> lowestValueEntropyEntries = (from entry in sortedEntropy where entry.Item1 == sortedEntropy[0].Item1 select entry).ToList();

        Vector2I selectedCell = lowestValueEntropyEntries[m_Random.Next(lowestValueEntropyEntries.Count)].Item2;
        //GD.Print(m_Tiles[selectedCell.X, selectedCell.Y].options.Count);
        //foreach (int option in m_Tiles[selectedCell.X, selectedCell.Y].options)
        //{
        //GD.Print("Valid option: " + option);
        //}

        int selectedOption = -1;
        int randomNum = 0;
        try
        {
            randomNum = m_Random.Next(m_Tiles[selectedCell.X, selectedCell.Y].options.Count);
            selectedOption = m_Tiles[selectedCell.X, selectedCell.Y].options[randomNum];
        }
        catch (Exception ex)
        {
            GD.Print(ex.Message);
            GD.Print("m_Tiles[", selectedCell.X, ", ", selectedCell.Y, "]");
            selectedOption = 0;
        }
        //GD.Print("Selected option: " + selectedOption);

        m_Tiles[selectedCell.X, selectedCell.Y] = new Tile(selectedOption, new List<int>(), true);
        m_TileMapLayer.SetCell(selectedCell, 0, m_KnownTiles[m_Tiles[selectedCell.X, selectedCell.Y].tileId].atlasCoords);
        //GD.Print("Atlas coord: " + m_KnownTiles[m_Tiles[selectedCell.X, selectedCell.Y].tileId].atlasCoords);

        return selectedCell;
    }

    public void Cycle()
    {
        if (m_Finished) return;

        Vector2I collapsedCell = Collapse();
        //GD.Print(collapsedCell);

        Constrain(collapsedCell, Direction.NONE);
    }

    void OnTimerTimeout()
    {
        //Cycle();
    }
}