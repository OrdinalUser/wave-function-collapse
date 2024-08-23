using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public struct Tile
{
    List<int> options; // holds ids of all possible tiles this tile can become
    int GetEntropy() { return options.Count; }
}

public struct TilePrototype
{
    public string sprite;
    public string name;
    public float weight;
    public string[] northConnections;
    public string[] southConnections;
    public string[] westConnections;
    public string[] eastConnections;
}

public partial class WaveFunctionCollapse : Node2D
{
    [Export] public int m_Seed = 0;
    [Export] public int m_Width = 20;
    [Export] public int m_Height = 20;
    [Export] public int m_SpriteSize = 64;

    public Tile[,] m_Tiles;

    public Dictionary<string, TilePrototype> m_KnownTiles;
    public Dictionary<string, int> m_KnownTilesToId;
    public string[] m_KnownTilesNames;
    public List<Vector2I> m_KnownTilesAtlasCoords;

    private Random m_Random;

    public TileMapLayer m_TileMapLayer;
    public TileSet m_TileSet;
    public Image m_Spritesheet;
    public ImageTexture m_AtlasTexture;
    public TileSetAtlasSource m_TileSetAtlasSource;

    bool LoadConfig(string configPath)
    {
        if (!FileAccess.FileExists(configPath))
        {
            GD.PushError("Config file not found!");
            return false;
        }

        FileAccess configFile = FileAccess.Open(configPath, FileAccess.ModeFlags.Read);

        Json json = new Json();
        var err = json.Parse(configFile.GetAsText());
        configFile.Close();
        if (err != Error.Ok)
        {
            GD.PushError("Failed to parse config!");
            return false;
        }


        using var config = json.Data.AsGodotDictionary();
        int tileId = 0;
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

            m_KnownTiles[tileName] = tile;
            m_KnownTilesToId[tileName] = tileId;
        }
        m_KnownTilesNames = m_KnownTiles.Keys.ToArray();

        var meta = config["Meta"].AsGodotDictionary();
        m_SpriteSize = meta["SpriteSize"].AsInt32();

        return true;
    }

    bool SetupTileMap()
    {
        int atlasSize = Mathf.CeilToInt(Mathf.Sqrt(m_KnownTiles.Count));
        int atlasDimension = atlasSize * m_SpriteSize;

        m_Spritesheet = new Image();
        m_Spritesheet = Image.CreateEmpty(atlasDimension, atlasDimension, false, Image.Format.Rgba8);

        int sizePerPicture = m_SpriteSize * m_SpriteSize * 4;
        byte[] stitchedData = new byte[atlasDimension * atlasDimension * 4];

        int pictureIndex = 0;

        foreach (var tile in m_KnownTiles.Values)
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
                int pixelIndex = ((pixelI / m_SpriteSize) * atlasDimension) + (pixelI % m_SpriteSize); // i=40 == 40 : Good =(floor(40/64)*192) + (40 % 64)
                pixelIndex += (pictureIndex % atlasSize * m_SpriteSize) + (pictureIndex / atlasSize * m_SpriteSize * atlasDimension); // Picture index=4 == =(2%64*64) + (floor(2/3)*64*192)
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
        m_TileSetAtlasSource.TextureRegionSize = new Vector2I(m_SpriteSize, m_SpriteSize);
        m_TileSetAtlasSource.UseTexturePadding = false;

        m_KnownTilesAtlasCoords = new List<Vector2I>();
        for (int i = 0; i < m_KnownTiles.Count; i++)
        {
            m_KnownTilesAtlasCoords.Add(new Vector2I(i % atlasSize, i / atlasSize));
            m_TileSetAtlasSource.CreateTile(m_KnownTilesAtlasCoords[i]);
            GD.Print("Id: " + i + ", name: " + m_KnownTiles[m_KnownTilesNames[i]].name + ", atlas: " + m_KnownTilesAtlasCoords[i]);
        }

        m_TileSet = new TileSet();
        m_TileSet.AddSource(m_TileSetAtlasSource, 0);
        m_TileSet.ResourceName = "Tile Atlas";
        m_TileSet.TileSize = new Vector2I(m_SpriteSize, m_SpriteSize);

        m_TileMapLayer = new TileMapLayer();
        m_TileMapLayer.Transform.Translated(new Vector2(50, 50));
        m_TileMapLayer.TileSet = m_TileSet;
        m_TileMapLayer.NavigationEnabled = false;
        m_TileMapLayer.CollisionEnabled = false;

        AddChild(m_TileMapLayer);

        return true;
    }

    const string CONFIG_PATH = "res://Rules.json";
    public override void _Ready()
    {
        m_Random = new Random(m_Seed);
        m_KnownTiles = new Dictionary<string, TilePrototype>();
        m_KnownTilesToId = new Dictionary<string, int>();

        LoadConfig(CONFIG_PATH);
        SetupTileMap();

        for (int i = 0; i < m_Width; i++)
        {
            for (int j = 0; j < m_Height; j++)
            {
                m_TileMapLayer.SetCell(new Vector2I(i, j), 0, m_KnownTilesAtlasCoords[(i % m_KnownTiles.Count + j * m_KnownTiles.Count) % m_KnownTiles.Count]);
            }
        }
    }

    public override void _Process(double delta)
    {

    }
}