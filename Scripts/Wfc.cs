using Godot;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;

public partial class Wfc : Node
{
    public struct TilePrototype
    {
        public string sprite; // Path to sprite file
        public Vector2I atlasCoords;

        public string model; // Path to model file

        public string name;
        public float weight;

        public int[] northConnections;
        public int[] westConnections;
        public int[] eastConnections;
        public int[] southConnections;
    }

    public struct Config
    {
        public int seed;
        public int columns;
        public int rows;
        public int spriteSize;

        public bool is3D;
        public float meshSize;
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

    [Export] public string RulesetFilePath = "res://Rules/Rules.json";

    public Tile[,] m_Tiles;
    public TilePrototype[] m_KnownTiles;
    public Dictionary<string, int> m_NamedKnownTiles;

    public bool m_Finished;

    private Random m_Random;

    /* 2D render stuff */
    private TileMapLayer m_TileMapLayer;
    private TileSet m_TileSet;
    private Image m_Spritesheet;
    private ImageTexture m_AtlasTexture;
    private TileSetAtlasSource m_TileSetAtlasSource;

    /* 3D render stuff */
    private List<Mesh> m_ModelMeshes;
    private Vector3 m_TileOffset;
    Node3D m_TileParent;

    GridMap m_GridMap;
    MeshLibrary m_MeshLibrary;

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

        using var config = json.Data.AsGodotDictionary();

        /* Load Config Data */
        m_Config = new Config();
        var meta = config["Meta"].AsGodotDictionary();
        m_Config.spriteSize = meta["SpriteSize"].AsInt32();
        m_Config.seed = meta["Seed"].AsInt32();
        if (m_Config.seed == 0)
        {
            Random r = new Random();
            m_Config.seed = r.Next();
        }
        m_Random = new Random(m_Config.seed);
        m_Config.rows = meta["Rows"].AsInt32();
        m_Config.columns = meta["Columns"].AsInt32();

        m_Config.is3D = meta["3D"].AsBool();
        m_Config.meshSize = (float)meta["MeshSize"];

        /* Load Tile Names */
        m_NamedKnownTiles = new Dictionary<string, int>();
        int tileId = 0;
        foreach (var tileName in config["Tiles"].AsStringArray())
        {
            m_NamedKnownTiles[tileName] = tileId;
            tileId++;
        }

        /* Load Tile Data */
        List<TilePrototype> tilePrototypes = new List<TilePrototype>();
        foreach (var tileName in config["Tiles"].AsStringArray())
        {
            using var tileData = config[tileName].AsGodotDictionary();
            TilePrototype tile = new TilePrototype();

            tile.name = tileName;
            tile.weight = (float)tileData["Weight"];
            if (!m_Config.is3D) tile.sprite = (string)tileData["Sprite"];
            else tile.model = (string)tileData["Model"];

            List<int> connections = new List<int>();
            foreach (var conn in tileData["North"].AsStringArray())
            {
                connections.Add(m_NamedKnownTiles[conn]);
            }
            tile.northConnections = connections.ToArray();

            connections = new List<int>();
            foreach (var conn in tileData["East"].AsStringArray())
            {
                connections.Add(m_NamedKnownTiles[conn]);
            }
            tile.eastConnections = connections.ToArray();

            connections = new List<int>();
            foreach (var conn in tileData["South"].AsStringArray())
            {
                connections.Add(m_NamedKnownTiles[conn]);
            }
            tile.southConnections = connections.ToArray();

            connections = new List<int>();
            foreach (var conn in tileData["West"].AsStringArray())
            {
                connections.Add(m_NamedKnownTiles[conn]);
            }
            tile.westConnections = connections.ToArray();

            tilePrototypes.Add(tile);
        }
        m_KnownTiles = tilePrototypes.ToArray();

        GD.Print("[Config]");
        GD.Print("Seed: ", m_Config.seed);

        return true;
    }

    bool InitializeGraphics()
    {
        if (!m_Config.is3D) return SetupTileMap();
        else return SetupTileMap3D();
    }

    bool SetupTileMap3D()
    {
        m_MeshLibrary = new MeshLibrary();
        for (int i = 0; i < m_KnownTiles.Length; i++)
        {
            ref TilePrototype tile = ref m_KnownTiles[i];

            ArrayMesh mesh = (ArrayMesh)ResourceLoader.Load("res://3D/" + tile.model);

            m_MeshLibrary.CreateItem(i);
            m_MeshLibrary.SetItemMesh(i, mesh);
        }

        m_GridMap = new GridMap();
        m_GridMap.MeshLibrary = m_MeshLibrary;
        m_GridMap.CellSize = new Vector3(m_Config.meshSize, m_Config.meshSize, m_Config.meshSize);
        m_GridMap.Name = "Grid map";
        m_GridMap.Translate(new Vector3(-m_Config.meshSize * m_Config.columns / 2, 0, -m_Config.meshSize * m_Config.rows / 2));
        AddChild(m_GridMap);

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
        //m_Spritesheet.SavePng("res://Temp/output.png");

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

        Vector2I screenSize = DisplayServer.WindowGetSize();
        Vector2I requiredSizeInPixels = new Vector2I(m_Config.columns * m_Config.spriteSize, m_Config.rows * m_Config.spriteSize);
        float finalScale = Mathf.Min((float)screenSize.X / requiredSizeInPixels.X, (float)screenSize.Y / requiredSizeInPixels.Y);
        m_TileMapLayer.Scale = new Vector2(finalScale, finalScale);

        AddChild(m_TileMapLayer);

        return true;
    }

    public override void _Ready()
    {
        m_Finished = false;
        if (!LoadConfig()) { GD.PrintErr("Failed to load config."); return; }
        if (!InitializeGraphics()) { GD.PrintErr("Failed to build tiles."); return; }

        m_Tiles = new Tile[m_Config.columns, m_Config.rows];
        for (int y = 0; y < m_Config.rows; y++)
        {
            for (int x = 0; x < m_Config.columns; x++)
            {
                m_Tiles[x, y] = new Tile(m_KnownTiles.Length);
            }
        }
    }

    private List<int> GetTilePossibleOptions(ref Tile tile, ref Tile checkedTile, Direction fromWhere)
    {
        List<int> directionOptions = new List<int>();
        for (int i = 0; i < m_KnownTiles.Length; i++)
        {
            directionOptions.Add(i);
        }

        int[] checkedTileOptionsSide = new int[0];
        if (checkedTile.collapsed)
        {
            if (fromWhere == Direction.LEFT) checkedTileOptionsSide = m_KnownTiles[checkedTile.tileId].westConnections;
            else if (fromWhere == Direction.RIGHT) checkedTileOptionsSide = m_KnownTiles[checkedTile.tileId].eastConnections;
            else if (fromWhere == Direction.UP) checkedTileOptionsSide = m_KnownTiles[checkedTile.tileId].northConnections;
            else if (fromWhere == Direction.DOWN) checkedTileOptionsSide = m_KnownTiles[checkedTile.tileId].southConnections;

            List<int> tileOptions = new List<int>();
            foreach (int option in checkedTileOptionsSide)
            {
                tileOptions.Add(option);
            }
            directionOptions = tileOptions;
        }
        else
        {
            foreach (int option in checkedTile.options)
            {
                List<int> tileOptions = new List<int>();
                if (fromWhere == Direction.LEFT) checkedTileOptionsSide = m_KnownTiles[option].westConnections;
                else if (fromWhere == Direction.RIGHT) checkedTileOptionsSide = m_KnownTiles[option].eastConnections;
                else if (fromWhere == Direction.UP) checkedTileOptionsSide = m_KnownTiles[option].northConnections;
                else if (fromWhere == Direction.DOWN) checkedTileOptionsSide = m_KnownTiles[option].southConnections;
                foreach (int o in checkedTileOptionsSide)
                {
                    tileOptions.Add(o);
                }
                directionOptions = directionOptions.Union(tileOptions).ToList();
            }
        }
        return directionOptions;
    }

    private bool Reduce(Vector2I cellPos)
    {
        if (cellPos.X < 0 || cellPos.Y < 0 || cellPos.X > m_Config.columns || cellPos.Y > m_Config.rows) return false;
        if (m_Tiles[cellPos.X, cellPos.Y].collapsed) return false;

        int x = cellPos.X, y = cellPos.Y;
        ref Tile tile = ref m_Tiles[x, y];

        bool changed = false;
        List<int> newOptions = new List<int>();
        for (int i = 0; i < m_KnownTiles.Length; i++)
        {
            newOptions.Add(i);
        }

        if (x > 0)
        {
            ref Tile checkedTile = ref m_Tiles[x - 1, y];
            List<int> directionOptions = GetTilePossibleOptions(ref tile, ref checkedTile, Direction.RIGHT);
            newOptions = newOptions.Intersect(directionOptions).ToList();
        }
        if (y > 0)
        {
            ref Tile checkedTile = ref m_Tiles[x, y - 1];
            List<int> directionOptions = GetTilePossibleOptions(ref tile, ref checkedTile, Direction.DOWN);
            newOptions = newOptions.Intersect(directionOptions).ToList();
        }
        if (x < m_Config.columns - 1)
        {
            ref Tile checkedTile = ref m_Tiles[x + 1, y];
            List<int> directionOptions = GetTilePossibleOptions(ref tile, ref checkedTile, Direction.LEFT);
            newOptions = newOptions.Intersect(directionOptions).ToList();
        }
        if (y < m_Config.rows - 1)
        {
            ref Tile checkedTile = ref m_Tiles[x, y + 1];
            List<int> directionOptions = GetTilePossibleOptions(ref tile, ref checkedTile, Direction.UP);
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

    private List<Vector2I> m_Visited;

    private void Constrain(Vector2I cellPos, Direction fromWhere)
    {
        if (m_Visited.Contains(cellPos)) { return; }
        m_Visited.Add(cellPos);
        int x = cellPos.X, y = cellPos.Y;

        if (x > 0 && fromWhere != Direction.LEFT)
        {
            if (Reduce(cellPos - new Vector2I(1, 0)))
                Constrain(cellPos - new Vector2I(1, 0), Direction.RIGHT);
        }
        if (y > 0 && fromWhere != Direction.UP)
        {
            if (Reduce(cellPos - new Vector2I(0, 1)))
                Constrain(cellPos - new Vector2I(0, 1), Direction.DOWN);
        }
        if (x < m_Config.columns - 1 && fromWhere != Direction.RIGHT)
        {
            if (Reduce(cellPos + new Vector2I(1, 0)))
                Constrain(cellPos + new Vector2I(1, 0), Direction.LEFT);
        }
        if (y < m_Config.rows - 1 && fromWhere != Direction.DOWN)
        {
            if (Reduce(cellPos + new Vector2I(0, 1)))
                Constrain(cellPos + new Vector2I(0, 1), Direction.UP);
        }

        if (fromWhere == Direction.NONE) Reduce(cellPos);
    }

    private int SelectOption(ref Tile tile)
    {
        List<int> weightedList = new List<int>();
        foreach (var o in tile.options)
        {
            for (int i = 0; i < m_KnownTiles[o].weight; i++)
            {
                weightedList.Add(o);
            }
        }

        return weightedList[m_Random.Next(weightedList.Count)];
    }

    private void ClearVisualGrid(Vector2I cell)
    {
        if (!m_Config.is3D) m_TileMapLayer.SetCell(cell);
        else m_GridMap.SetCellItem(new Vector3I(cell.X, 0, cell.Y), -1);
    }

    private void SetVisualGrid(Vector2I cell, int tileId)
    {
        if (!m_Config.is3D) m_TileMapLayer.SetCell(cell, 0, m_KnownTiles[m_Tiles[cell.X, cell.Y].tileId].atlasCoords);
        else m_GridMap.SetCellItem(new Vector3I(cell.X, 0, cell.Y), tileId);
    }

    private void UnCollapse(Vector2I cellPos)
    {
        // This can probably be better, Optimize
        m_Tiles[cellPos.X, cellPos.Y] = new Tile(m_KnownTiles.Length);
        ClearVisualGrid(cellPos);

        // Straight
        if (cellPos.X > 0)
        {
            m_Tiles[cellPos.X - 1, cellPos.Y] = new Tile(m_KnownTiles.Length);
            ClearVisualGrid(cellPos - new Vector2I(1, 0));
        }
        if (cellPos.Y > 0)
        {
            m_Tiles[cellPos.X, cellPos.Y - 1] = new Tile(m_KnownTiles.Length);
            ClearVisualGrid(cellPos - new Vector2I(0, 1));
        }
        if (cellPos.X < m_Config.columns - 1)
        {
            m_Tiles[cellPos.X + 1, cellPos.Y] = new Tile(m_KnownTiles.Length);
            ClearVisualGrid(cellPos + new Vector2I(1, 0));
        }
        if (cellPos.Y < m_Config.rows - 1)
        {
            m_Tiles[cellPos.X, cellPos.Y + 1] = new Tile(m_KnownTiles.Length);
            ClearVisualGrid(cellPos + new Vector2I(0, 1));
        }

        // Far ends
        if (cellPos.X - 1 > 0)
        {
            m_Tiles[cellPos.X - 2, cellPos.Y] = new Tile(m_KnownTiles.Length);
            ClearVisualGrid(cellPos - new Vector2I(2, 0));
        }
        if (cellPos.Y - 1 > 0)
        {
            m_Tiles[cellPos.X, cellPos.Y - 2] = new Tile(m_KnownTiles.Length);
            ClearVisualGrid(cellPos - new Vector2I(0, 2));
        }
        if (cellPos.Y < m_Config.rows - 2)
        {
            m_Tiles[cellPos.X, cellPos.Y + 2] = new Tile(m_KnownTiles.Length);
            ClearVisualGrid(cellPos + new Vector2I(0, 2));
        }
        if (cellPos.X < m_Config.columns - 2)
        {
            m_Tiles[cellPos.X + 2, cellPos.Y] = new Tile(m_KnownTiles.Length);
            ClearVisualGrid(cellPos + new Vector2I(2, 0));
        }

        m_Visited.Clear();
        if (cellPos.Y > 0) Constrain(cellPos - new Vector2I(0, 1), Direction.NONE);
        if (cellPos.X > 0) Constrain(cellPos - new Vector2I(1, 0), Direction.NONE);
        if (cellPos.X < m_Config.columns - 1) Constrain(cellPos + new Vector2I(1, 0), Direction.NONE);
        if (cellPos.Y < m_Config.rows - 1) Constrain(cellPos + new Vector2I(0, 1), Direction.NONE);
        m_Visited.Clear();

        Constrain(cellPos, Direction.NONE);
        m_Visited.Clear();
    }


    private bool firstCollapse = true;
    private Vector2I Collapse()
    {
        Vector2I selectedCell = Vector2I.Zero;
        if (!firstCollapse)
        {
            // Optimize
            List<Tuple<int, Vector2I>> entropyList = new List<Tuple<int, Vector2I>>();
            for (int y = 0; y < m_Config.rows; y++)
            {
                for (int x = 0; x < m_Config.columns; x++)
                {
                    if (!m_Tiles[x, y].collapsed) entropyList.Add(new Tuple<int, Vector2I>(m_Tiles[x, y].GetEntropy(), new Vector2I(x, y)));
                }
            }
            if (entropyList.Count == 0) { m_Finished = true; return new Vector2I(-1, -1); }

            List<Tuple<int, Vector2I>> sortedEntropy = entropyList.OrderBy(o => o.Item1).ToList();
            List<Tuple<int, Vector2I>> lowestValueEntropyEntries = (from entry in sortedEntropy where entry.Item1 == sortedEntropy[0].Item1 select entry).ToList();

            selectedCell = lowestValueEntropyEntries[m_Random.Next(lowestValueEntropyEntries.Count)].Item2;
        }
        else { selectedCell = new Vector2I(m_Config.rows / 2, m_Config.columns / 2); firstCollapse = false; }

        if (m_Tiles[selectedCell.X, selectedCell.Y].GetEntropy() != 0)
        {
            int randomNum = m_Random.Next(m_Tiles[selectedCell.X, selectedCell.Y].options.Count);
            int selectedOption = SelectOption(ref m_Tiles[selectedCell.X, selectedCell.Y]);

            m_Tiles[selectedCell.X, selectedCell.Y] = new Tile(selectedOption, new List<int>(), true);
            SetVisualGrid(selectedCell, selectedOption);

            return selectedCell;
        }
        else
        {
            UnCollapse(selectedCell);
            return new Vector2I(-1, -1);
        }
    }

    public void Cycle()
    {
        if (m_Finished) return;

        m_Visited = new List<Vector2I>();
        Vector2I collapsedCell = Collapse();
        if (collapsedCell.X == -1) { if (m_Finished) OnFinishGeneration(); return; }

        Constrain(collapsedCell, Direction.NONE);
    }

    private void OnFinishGeneration()
    {
        GD.Print("[Metrics]");
        GD.Print("Total time taken: ", timeAcc / (1000.0f * 1000.0f), " seconds");
        GD.Print("Average time per cycle: ", timeAcc / (double)cycleCount, "us");
    }

    private long cycleCount = 0, timeAcc = 0;
    public override void _Process(double delta)
    {
        if (!m_Finished)
        {
            Stopwatch sw = new Stopwatch();
            for (int i = 0; i < 25; i++)
            {
                if (m_Finished) break;
                //if (Input.IsActionJustPressed("Cycle"))
                //{
                sw.Start();
                Cycle();
                sw.Stop();
                //}
                cycleCount++;
                long microseconds = sw.ElapsedTicks / (Stopwatch.Frequency / (1000L * 1000L));
                timeAcc += microseconds;
                sw.Reset();
            }
        }
    }

    void OnTimerTimeout()
    {
        //Cycle();
    }
}