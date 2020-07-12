using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Server.Controllers
{
    [ApiController]
    [Route("/api/rooms")]
    public class RoomsController : ControllerBase
    {
        private readonly ILogger<RoomsController> _logger;

        public RoomsController(ILogger<RoomsController> logger)
        {
            _logger = logger;
        }

        [HttpGet]
        public IEnumerable<Room> Get()
        {
            return new List<Room>
            {
                new Room { Id = "politics", Name = "Politics", Description = "Incoherent screeching"},
                new Room { Id = "dank-memes", Name = "Dank Memes", Description = "Mostly dril tweets"},
                new Room { Id = "technology", Name = "Technology", Description = "Like HackerNews, but even worse"},
            };
        }
    }
}
